// Experimental controller: only the current session's exact system dwm.exe is
// ever a remote-write target. The selected application receives read-only access.
#define HDR_SELECTIVE_PREFLIGHT_LIBRARY
#include "Preflight.cpp"
#include "../include/SelectiveDwmProtocol.h"
#include "../include/SelectiveDwmPolicy.h"
#include <sddl.h>
#include <cwchar>
#include <algorithm>
#include <cstring>
#include <cerrno>
#include <iterator>
#include <cstdarg>
#include <deque>

using namespace selective_dwm;

namespace {
struct Options {
    bool veto = false;
    bool inspectDll = false;
    bool validateOnly = false;
    DWORD targetPid = 0;
    std::uint64_t targetHwnd = 0;
    DWORD expectedDwmPid = 0;
    std::uint64_t expectedDwmCreation = 0;
    std::uint64_t targetCreation = 0;
    DWORD ownerPid = 0;
    std::uint64_t ownerCreation = 0;
    bool continuous = false;
    DWORD durationMs = 5000;
    std::wstring dll;
    std::wstring targetExe;
    std::wstring pipe;
};
std::int64_t Qpc() { LARGE_INTEGER v{}; QueryPerformanceCounter(&v); return v.QuadPart; }
std::int64_t Frequency() { LARGE_INTEGER v{}; QueryPerformanceFrequency(&v); return v.QuadPart; }
bool Number(const wchar_t* text, std::uint64_t maximum, std::uint64_t& result) {
    if (!text || !*text || *text == L'-' || *text == L'+') return false;
    if (*text < L'0' || *text > L'9') return false;
    wchar_t* end = nullptr;
    errno = 0;
    const auto value = wcstoull(text, &end, 0);
    if (errno || !end || *end || value > maximum) return false;
    result = value; return true;
}
bool AbsoluteLocalPath(const std::wstring& path) {
    // An explicit selected executable is an ordinary absolute DOS path. Do not
    // resolve relative names, UNC/device namespaces, alternate streams or path
    // aliases that could change the meaning of the user's selected identity.
    if (path.size() < 4 || path.size() >= 32768 || path[1] != L':' || path[2] != L'\\' ||
        !((path[0] >= L'A' && path[0] <= L'Z') || (path[0] >= L'a' && path[0] <= L'z'))) return false;
    std::size_t component = 3;
    for (std::size_t i = component; i <= path.size(); ++i) {
        if (i == path.size() || path[i] == L'\\') {
            if (i == component || path[i - 1] == L'.' || path[i - 1] == L' ') return false;
            component = i + 1;
        } else if (path[i] < 32 || wcschr(L"/:\"<>|?*", path[i])) return false;
    }
    return true;
}
std::string JsonString(const std::wstring& value) {
    // ASCII JSON preserves non-ASCII UTF-16 (including surrogate pairs) without
    // depending on the controller's console code page or C runtime locale.
    std::string result = "\"";
    for (const auto character : value) {
        if (character == L'\\' || character == L'\"') {
            result += '\\'; result += static_cast<char>(character);
        } else if (character < 32 || character >= 127) {
            char escaped[7]{};
            sprintf_s(escaped, "\\u%04x", static_cast<unsigned int>(character));
            result += escaped;
        } else result += static_cast<char>(character);
    }
    result += '\"';
    return result;
}
bool ValidPipeName(const std::wstring& name) {
    constexpr wchar_t prefix[] = L"HDRCalibrate.ProgramGamma.";
    constexpr std::size_t prefixLength = std::size(prefix) - 1;
    if (name.size() != prefixLength + 32 || name.compare(0, prefixLength, prefix)) return false;
    return std::all_of(name.begin() + prefixLength, name.end(), [](wchar_t ch) {
        return (ch >= L'0' && ch <= L'9') || (ch >= L'a' && ch <= L'f') || (ch >= L'A' && ch <= L'F');
    });
}
bool Parse(int argc, wchar_t** argv, Options& result) {
    bool hasMode = false;
    bool hasDuration = false;
    for (int i = 1; i < argc; ++i) {
        const std::wstring arg = argv[i];
        if (arg == L"--observe" || arg == L"--veto" || arg == L"--inspect-dll") {
            if (hasMode) return false;
            hasMode = true; result.veto = arg == L"--veto"; result.inspectDll = arg == L"--inspect-dll";
        } else if (arg == L"--validate-only") {
            if (result.validateOnly) return false;
            result.validateOnly = true;
        } else if (arg == L"--continuous") {
            if (result.continuous) return false;
            result.continuous = true;
        } else if (arg == L"--dll") {
            if (++i == argc || !result.dll.empty()) return false; result.dll = argv[i];
        } else if (arg == L"--target-exe") {
            if (++i == argc || !result.targetExe.empty()) return false;
            result.targetExe = argv[i];
            if (!AbsoluteLocalPath(result.targetExe)) return false;
        } else if (arg == L"--pipe") {
            if (++i == argc || !result.pipe.empty()) return false;
            result.pipe = argv[i];
            if (!ValidPipeName(result.pipe)) return false;
        } else if (arg == L"--target-pid" || arg == L"--target-hwnd" || arg == L"--duration-ms" ||
            arg == L"--expected-dwm-pid" || arg == L"--expected-dwm-creation-filetime" ||
            arg == L"--target-creation-filetime" || arg == L"--owner-pid" || arg == L"--owner-creation-filetime") {
            if (++i == argc) return false;
            std::uint64_t value = 0;
            const std::uint64_t maximum = arg == L"--target-hwnd" ? UINT64_MAX :
                (arg == L"--expected-dwm-creation-filetime" || arg == L"--target-creation-filetime" || arg == L"--owner-creation-filetime") ?
                    static_cast<std::uint64_t>(INT64_MAX) : MAXDWORD;
            if (!Number(argv[i], maximum, value) || !value) return false;
            if (arg == L"--target-pid") { if (result.targetPid) return false; result.targetPid = static_cast<DWORD>(value); }
            if (arg == L"--target-hwnd") { if (result.targetHwnd) return false; result.targetHwnd = value; }
            if (arg == L"--expected-dwm-pid") {
                if (result.expectedDwmPid) return false; result.expectedDwmPid = static_cast<DWORD>(value);
            }
            if (arg == L"--expected-dwm-creation-filetime") {
                if (result.expectedDwmCreation) return false; result.expectedDwmCreation = value;
            }
            if (arg == L"--target-creation-filetime") {
                if (result.targetCreation) return false; result.targetCreation = value;
            }
            if (arg == L"--owner-pid") {
                if (result.ownerPid) return false; result.ownerPid = static_cast<DWORD>(value);
            }
            if (arg == L"--owner-creation-filetime") {
                if (result.ownerCreation) return false; result.ownerCreation = value;
            }
            if (arg == L"--duration-ms") {
                if (hasDuration) return false;
                hasDuration = true; result.durationMs = static_cast<DWORD>(value);
            }
        } else return false;
    }
    return hasMode && !result.dll.empty() && result.durationMs <= 5000 && result.durationMs >= 100 &&
        (result.inspectDll || (result.targetPid && result.targetHwnd)) &&
        ((result.expectedDwmPid == 0) == (result.expectedDwmCreation == 0)) &&
        ((result.targetCreation == 0) == result.targetExe.empty()) &&
        ((result.ownerPid == 0) == (result.ownerCreation == 0)) &&
        ((result.ownerPid == 0) == result.pipe.empty()) &&
        (!result.ownerPid || (result.targetCreation && result.expectedDwmPid && !result.inspectDll)) &&
        (!result.continuous || result.ownerPid) &&
        !(result.inspectDll && (result.expectedDwmPid || !result.targetExe.empty()));
}
DWORD VerifyTargetMetadata(HANDLE target, const Options& options, std::uint64_t& created, std::wstring& executable) {
    const auto pid = GetProcessId(target);
    if (!pid) return GetLastError();
    if (pid != options.targetPid) return ERROR_INVALID_OWNER;
    const auto initiallyAlive = WaitForSingleObject(target, 0);
    if (initiallyAlive != WAIT_TIMEOUT) return initiallyAlive == WAIT_FAILED ? GetLastError() : ERROR_PROCESS_ABORTED;
    const auto processCreation = CreationTime(target);
    if (!processCreation || (options.targetCreation && processCreation != options.targetCreation)) return ERROR_INVALID_DATA;
    wchar_t image[32768]{};
    DWORD length = static_cast<DWORD>(std::size(image));
    if (!QueryFullProcessImageNameW(target, 0, image, &length)) return GetLastError();
    if (!length || length >= std::size(image)) return ERROR_BAD_PATHNAME;
    const std::wstring processImage(image, length);
    if (!options.targetExe.empty()) {
        if (!AbsoluteLocalPath(processImage) || CompareStringOrdinal(processImage.c_str(), -1,
            options.targetExe.c_str(), -1, TRUE) != CSTR_EQUAL) return ERROR_INVALID_NAME;
        const wchar_t root[]{ processImage[0], L':', L'\\', L'\0' };
        const auto drive = GetDriveTypeW(root);
        if (drive != DRIVE_FIXED && drive != DRIVE_REMOVABLE && drive != DRIVE_CDROM && drive != DRIVE_RAMDISK)
            return ERROR_BAD_PATHNAME;
    }
    const auto wait = WaitForSingleObject(target, 0);
    if (wait != WAIT_TIMEOUT) return wait == WAIT_FAILED ? GetLastError() : ERROR_PROCESS_ABORTED;
    // Only publish a complete snapshot. The retained handle keeps this process
    // object, creation time and image identity together for the entire request.
    created = processCreation;
    executable = processImage;
    return ERROR_SUCCESS;
}
struct ManagedCommands {
    bool committed = false;
    bool stopped = false;
    bool failed = false;
    std::int64_t heartbeatQpc = 0;
};
void AcceptManagedCommand(ManagedCommands& state, BYTE command, std::int64_t now) {
    if (state.stopped || state.failed) return;
    if (command == 1) state.heartbeatQpc = now;
    else if (command == 2) state.stopped = true;
    else if (command == 3 && !state.committed) { state.committed = true; state.heartbeatQpc = now; }
    else state.failed = true;
}
bool ManagedHeartbeatFresh(const ManagedCommands& state, std::int64_t now, std::int64_t frequency) {
    return !state.failed && !state.stopped && state.committed && frequency > 0 && frequency <= INT64_MAX / 3 &&
        state.heartbeatQpc >= 0 && now >= state.heartbeatQpc && now - state.heartbeatQpc <= frequency * 3 / 2;
}
bool RenewManagedLease(Request& request, std::int64_t now, DWORD durationMs) {
    if (durationMs < 100 || durationMs > kMaximumLeaseMilliseconds ||
        ValidateLease(request, request, now, request.qpcFrequency) != Decision::Observe) return false;
    const auto ticks = (request.qpcFrequency / 1000) * durationMs +
        ((request.qpcFrequency % 1000) * durationMs) / 1000;
    if (ticks <= 0 || now > INT64_MAX - ticks) return false;
    request.leaseIssuedQpc = now;
    request.leaseDeadlineQpc = now + ticks;
    return true;
}
bool ManagedSurfaceRejected(const Request& request, const Sample& sample, std::int64_t now, std::int64_t armedQpc) {
    if (request.mode != Mode::CandidateVeto || request.qpcFrequency <= 0 || now < 0 || armedQpc < 0 ||
        sample.qpc < armedQpc || sample.qpc > now ||
        now - sample.qpc >= request.qpcFrequency || sample.point != HookPoint::Candidate || sample.stage != Stage::Complete ||
        !(sample.flags & OwnershipVerified) || sample.hwnd != request.targetHwnd || sample.ownerPid != request.targetPid) return false;
    if (sample.decision == Decision::SurfaceChanged || sample.decision == Decision::NotVerifiedSdr) return true;
    // A current, complete but different tuple is reported as MissingObservation
    // by the callback before it reaches the exact-tuple comparison. It is a
    // proven replacement only with fresh verified ownership and SDR metadata.
    return sample.decision == Decision::MissingObservation && (sample.flags & HasBindInfo) && (sample.flags & HasPixelFormat) &&
        sample.visual && sample.surface && sample.bind && IsVerifiedSdrFormat(sample.colorSpace, sample.pixelFormat) &&
        (sample.visual != request.expectedVisual || sample.surface != request.expectedSurface || sample.bind != request.expectedBind);
}
class ManagedPipe {
    Handle pipe_;
    Handle owner_;
    Handle readEvent_;
    Handle writeEvent_;
    OVERLAPPED read_{};
    OVERLAPPED write_{};
    BYTE command_ = 0;
    bool readPending_ = false;
    bool writePending_ = false;
    DWORD ownerPid_ = 0;
    std::uint64_t ownerCreation_ = 0;
    std::int64_t writeStarted_ = 0;
    std::int64_t frequency_ = 0;
    std::deque<std::string> messages_;
    std::size_t queuedBytes_ = 0;
    DWORD error_ = 0;
    ManagedCommands commands_;

    void Fail(DWORD error) { commands_.failed = true; if (!error_) error_ = error ? error : ERROR_INVALID_DATA; }
    bool OwnerAlive() const {
        return owner_.value && GetProcessId(owner_.value) == ownerPid_ &&
            WaitForSingleObject(owner_.value, 0) == WAIT_TIMEOUT && CreationTime(owner_.value) == ownerCreation_;
    }
    void ReadCommands() {
        // At most 64 control bytes per polling turn; unexpected traffic cannot
        // monopolize the controller or extend a lease by keeping it in a loop.
        for (unsigned int count = 0; count != 64 && !commands_.failed && !commands_.stopped; ++count) {
            DWORD received = 0;
            if (readPending_) {
                if (!GetOverlappedResult(pipe_.value, &read_, &received, FALSE)) {
                    const auto error = GetLastError();
                    if (error != ERROR_IO_INCOMPLETE) Fail(error);
                    return;
                }
                readPending_ = false;
            } else {
                ResetEvent(readEvent_.value);
                read_ = {}; read_.hEvent = readEvent_.value;
                if (!ReadFile(pipe_.value, &command_, 1, &received, &read_)) {
                    const auto error = GetLastError();
                    if (error == ERROR_IO_PENDING) readPending_ = true;
                    else Fail(error);
                    return;
                }
            }
            if (received != 1) { Fail(ERROR_BAD_LENGTH); return; }
            AcceptManagedCommand(commands_, command_, Qpc());
            if (commands_.failed) Fail(ERROR_INVALID_DATA);
        }
    }
    void WriteMessages() {
        for (unsigned int count = 0; count != 32 && !messages_.empty() && !commands_.failed; ++count) {
            DWORD written = 0;
            if (writePending_) {
                if (!GetOverlappedResult(pipe_.value, &write_, &written, FALSE)) {
                    const auto error = GetLastError();
                    if (error != ERROR_IO_INCOMPLETE) Fail(error);
                    else if (Qpc() - writeStarted_ > frequency_ / 2) Fail(ERROR_TIMEOUT);
                    return;
                }
                writePending_ = false;
            } else {
                ResetEvent(writeEvent_.value);
                write_ = {}; write_.hEvent = writeEvent_.value;
                writeStarted_ = Qpc();
                if (!WriteFile(pipe_.value, messages_.front().data(), static_cast<DWORD>(messages_.front().size()), &written, &write_)) {
                    const auto error = GetLastError();
                    if (error == ERROR_IO_PENDING) writePending_ = true;
                    else Fail(error);
                    return;
                }
            }
            if (written != messages_.front().size()) { Fail(ERROR_WRITE_FAULT); return; }
            queuedBytes_ -= messages_.front().size();
            messages_.pop_front();
        }
    }
public:
    // This one connection is allocated for the lifetime of the standalone
    // controller process. Its OVERLAPPED structures and queued write buffers
    // remain valid even on an exit with cancellation still pending; process
    // teardown closes the pipe. No desktop process owns these allocations.
    bool Connect(const Options& options) {
        frequency_ = Frequency();
        if (frequency_ <= 0 || frequency_ > INT64_MAX / 30) { Fail(ERROR_INVALID_DATA); return false; }
        ownerPid_ = options.ownerPid; ownerCreation_ = options.ownerCreation;
        owner_.value = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, ownerPid_);
        if (!OwnerAlive()) { Fail(ERROR_INVALID_OWNER); return false; }
        DWORD ownerSession = 0, controllerSession = 0;
        if (!ProcessIdToSessionId(ownerPid_, &ownerSession) ||
            !ProcessIdToSessionId(GetCurrentProcessId(), &controllerSession) || ownerSession != controllerSession) {
            Fail(ERROR_INVALID_OWNER); return false;
        }
        const auto path = L"\\\\.\\pipe\\" + options.pipe;
        const auto deadline = Qpc() + frequency_ * 30;
        do {
            pipe_.value = CreateFileW(path.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING,
                FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr);
            if (pipe_.value != INVALID_HANDLE_VALUE) break;
            const auto error = GetLastError();
            pipe_.value = nullptr;
            if (error != ERROR_PIPE_BUSY && error != ERROR_FILE_NOT_FOUND) { Fail(error); return false; }
            Sleep(10);
        } while (Qpc() < deadline && OwnerAlive());
        if (!pipe_.value || !OwnerAlive()) { Fail(ERROR_TIMEOUT); return false; }
        ULONG serverPid = 0;
        if (!GetNamedPipeServerProcessId(pipe_.value, &serverPid) || serverPid != ownerPid_) {
            Fail(ERROR_INVALID_OWNER); return false;
        }
        readEvent_.value = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        writeEvent_.value = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (!readEvent_.value || !writeEvent_.value) { Fail(GetLastError()); return false; }
        return true;
    }
    void Pump() {
        if (commands_.failed) return;
        if (!OwnerAlive()) { Fail(ERROR_PROCESS_ABORTED); return; }
        // A delayed polling turn cannot re-date queued old bytes and revive a
        // heartbeat that has already expired. A slow controller also fails
        // closed, even if the owner continued writing while it was stalled.
        if (commands_.committed && !commands_.stopped && !ManagedHeartbeatFresh(commands_, Qpc(), frequency_)) {
            Fail(ERROR_TIMEOUT); return;
        }
        ReadCommands();
        if (commands_.stopped || commands_.failed) return;
        WriteMessages();
        if (commands_.committed && !commands_.stopped && !ManagedHeartbeatFresh(commands_, Qpc(), frequency_)) Fail(ERROR_TIMEOUT);
    }
    bool WaitForGo() {
        const auto deadline = Qpc() + frequency_ * 30;
        while (!commands_.committed && !commands_.stopped && !commands_.failed && Qpc() < deadline) {
            Pump(); Sleep(10);
        }
        if (!commands_.committed && !commands_.stopped) Fail(ERROR_TIMEOUT);
        return Healthy();
    }
    bool Healthy() { Pump(); return ManagedHeartbeatFresh(commands_, Qpc(), frequency_); }
    bool StopRequested() const { return commands_.stopped; }
    DWORD Error() const { return error_; }
    void Queue(const std::string& message) {
        if (commands_.failed) return;
        if (message.size() > 16384 || queuedBytes_ + message.size() > 65536) { Fail(ERROR_BUFFER_OVERFLOW); return; }
        messages_.push_back(message); queuedBytes_ += message.size();
        WriteMessages();
    }
    void Drain() {
        const auto deadline = Qpc() + frequency_ / 5;
        while (!commands_.failed && !messages_.empty() && Qpc() < deadline) { WriteMessages(); Sleep(1); }
    }
};
ManagedPipe* g_managedPipe = nullptr;
void Emit(const char* format, ...) {
    va_list arguments;
    va_start(arguments, format);
    if (!g_managedPipe) { std::vprintf(format, arguments); va_end(arguments); return; }
    char buffer[16384]{};
    const auto length = vsnprintf_s(buffer, std::size(buffer), _TRUNCATE, format, arguments);
    va_end(arguments);
    if (length > 0) g_managedPipe->Queue(std::string(buffer, length));
}
bool ManagedHealthy() { return !g_managedPipe || g_managedPipe->Healthy(); }
struct Module { std::uint64_t base = 0; DWORD size = 0; std::wstring path; };
Module FindModule(DWORD pid, const std::wstring& exactPath) {
    Handle snapshot(CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid));
    if (snapshot.value == INVALID_HANDLE_VALUE) return {};
    MODULEENTRY32W entry{}; entry.dwSize = sizeof(entry);
    if (Module32FirstW(snapshot.value, &entry)) do {
        if (_wcsicmp(exactPath.c_str(), entry.szExePath) == 0)
            return { reinterpret_cast<std::uint64_t>(entry.modBaseAddr), entry.modBaseSize, entry.szExePath };
    } while (Module32NextW(snapshot.value, &entry));
    return {};
}
DWORD CheckExclusiveHookPath(DWORD pid, const std::wstring& exactPath, Module& conflict) {
    Handle snapshot(CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid));
    if (snapshot.value == INVALID_HANDLE_VALUE) return GetLastError();
    MODULEENTRY32W entry{}; entry.dwSize = sizeof(entry);
    if (!Module32FirstW(snapshot.value, &entry)) return GetLastError();
    do {
        if (_wcsicmp(entry.szModule, L"HDRCalibrate.SelectiveDwmHook.dll") == 0 &&
            CompareStringOrdinal(exactPath.c_str(), -1, entry.szExePath, -1, TRUE) != CSTR_EQUAL) {
            conflict = { reinterpret_cast<std::uint64_t>(entry.modBaseAddr), entry.modBaseSize, entry.szExePath };
            return ERROR_ALREADY_EXISTS;
        }
    } while (Module32NextW(snapshot.value, &entry));
    const auto error = GetLastError();
    return error == ERROR_NO_MORE_FILES ? ERROR_SUCCESS : error;
}
DWORD FindDwm() {
    DWORD session = 0; if (!ProcessIdToSessionId(GetCurrentProcessId(), &session)) return 0;
    Handle snapshot(CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0));
    if (snapshot.value == INVALID_HANDLE_VALUE) return 0;
    PROCESSENTRY32W e{}; e.dwSize = sizeof(e); DWORD found = 0;
    if (Process32FirstW(snapshot.value, &e)) do {
        DWORD candidateSession = 0;
        if (_wcsicmp(e.szExeFile, L"dwm.exe") == 0 && ProcessIdToSessionId(e.th32ProcessID, &candidateSession) && candidateSession == session) {
            if (found) return 0; found = e.th32ProcessID;
        }
    } while (Process32NextW(snapshot.value, &e));
    return found;
}
std::wstring SidOf(HANDLE process) {
    Handle token; if (!OpenProcessToken(process, TOKEN_QUERY, &token.value)) return {};
    DWORD bytes = 0; GetTokenInformation(token.value, TokenUser, nullptr, 0, &bytes);
    if (!bytes) return {};
    std::vector<BYTE> storage(bytes);
    if (!GetTokenInformation(token.value, TokenUser, storage.data(), bytes, &bytes)) return {};
    LPWSTR sid = nullptr;
    if (!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(storage.data())->User.Sid, &sid)) return {};
    const std::wstring value(sid); LocalFree(sid); return value;
}
std::uint64_t ExportRva(HMODULE image, const char* name) {
    const auto function = GetProcAddress(image, name);
    return function ? reinterpret_cast<std::uint64_t>(function) - reinterpret_cast<std::uint64_t>(image) : 0;
}
std::uint64_t RemoteEquivalent(DWORD pid, FARPROC function) {
    HMODULE owner = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        reinterpret_cast<LPCWSTR>(function), &owner)) return 0;
    wchar_t path[MAX_PATH]{}; if (!GetModuleFileNameW(owner, path, MAX_PATH)) return 0;
    const auto module = FindModule(pid, path);
    if (!module.base) return 0;
    const auto offset = reinterpret_cast<std::uint64_t>(function) - reinterpret_cast<std::uint64_t>(owner);
    return offset < module.size ? module.base + offset : 0;
}
struct RemoteCallResult { bool completed = false; DWORD result = ERROR_GEN_FAILURE; DWORD error = 0; };
RemoteCallResult RemoteCall(HANDLE process, std::uint64_t address, const void* payload, SIZE_T bytes) {
    RemoteCallResult result;
    void* remote = nullptr;
    if (bytes) {
        remote = VirtualAllocEx(process, nullptr, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (!remote) { result.error = GetLastError(); return result; }
        SIZE_T written = 0;
        if (!WriteProcessMemory(process, remote, payload, bytes, &written) || written != bytes) {
            result.error = GetLastError(); VirtualFreeEx(process, remote, 0, MEM_RELEASE); return result;
        }
    }
    Handle thread(CreateRemoteThread(process, nullptr, 0, reinterpret_cast<LPTHREAD_START_ROUTINE>(address), remote, 0, nullptr));
    if (!thread.value) {
        result.error = GetLastError(); if (remote) VirtualFreeEx(process, remote, 0, MEM_RELEASE); return result;
    }
    const auto wait = WaitForSingleObject(thread.value, 5000);
    if (wait == WAIT_OBJECT_0) {
        result.completed = GetExitCodeThread(thread.value, &result.result) != FALSE;
        if (!result.completed) result.error = GetLastError();
        if (remote) VirtualFreeEx(process, remote, 0, MEM_RELEASE);
    } else {
        // A timed-out remote call may still reference its argument. Never free
        // that memory, terminate that thread, or restart its host to "clean up".
        result.error = wait == WAIT_TIMEOUT ? ERROR_TIMEOUT : GetLastError();
        if (!g_managedPipe) Emit("{\"event\":\"remote_call_incomplete\",\"error\":%lu,\"argument_retained\":%s}\n", result.error, remote ? "true" : "false");
    }
    return result;
}
void Publish(SharedState* shared, const Request& request) {
    auto* sequence = reinterpret_cast<volatile LONG64*>(&shared->request.sequence);
    InterlockedIncrement64(sequence);
    std::memcpy(reinterpret_cast<BYTE*>(&shared->request) + sizeof(LONG64),
        reinterpret_cast<const BYTE*>(&request) + sizeof(LONG64), sizeof(Request) - sizeof(LONG64));
    MemoryBarrier(); InterlockedIncrement64(sequence);
}
bool SampleSnapshot(const Sample& source, Sample& result) {
    const auto first = InterlockedCompareExchange64(reinterpret_cast<volatile LONG64*>(const_cast<std::int64_t*>(&source.sequence)), 0, 0);
    if (first & 1) return false;
    std::memcpy(&result, &source, sizeof(result)); MemoryBarrier();
    const auto last = InterlockedCompareExchange64(reinterpret_cast<volatile LONG64*>(const_cast<std::int64_t*>(&source.sequence)), 0, 0);
    return first == last && !(last & 1) && last != 0;
}
void PrintTelemetry(const SharedState* state, const char* phase) {
    const auto& t = state->telemetry; Sample sample{}, latest{};
    const bool has = SampleSnapshot(t.latestTarget, sample);
    const bool hasLatest = SampleSnapshot(t.latest, latest);
    const auto startupStage = !hasLatest && (t.lifecycle == Lifecycle::Starting || t.lifecycle == Lifecycle::Failed)
        ? static_cast<unsigned>(t.latest.stage) : 0u;
    Emit("{\"event\":\"telemetry\",\"phase\":\"%s\",\"qpc\":%lld,\"lifecycle\":%u,\"capabilities\":%u,"
        "\"candidate_calls\":%lld,\"iflip_calls\":%lld,\"target_matches\":%lld,\"sdr_matches\":%lld,"
        "\"vetoed\":%lld,\"demoted\":%lld,\"read_faults\":%lld,\"active_callbacks\":%lld,\"expired_lease_calls\":%lld,"
        "\"sample_valid\":%s,\"sample_qpc\":%lld,\"sample_stage\":%u,\"point\":%u,\"decision\":%u,\"visual\":%llu,\"surface\":%llu,"
        "\"bind\":%llu,\"hwnd\":%llu,\"owner_pid\":%u,\"colorspace\":%u,\"format\":%u,\"flags\":%u,\"original_enable\":%u,"
        "\"latest_valid\":%s,\"latest_stage\":%u,\"latest_point\":%u,\"latest_visual\":%llu,\"latest_decision\":%u,"
        "\"latest_hwnd\":%llu,\"latest_owner_pid\":%u,\"latest_colorspace\":%u,\"latest_format\":%u,\"latest_flags\":%u,"
        "\"last_callback_qpc\":%lld,\"startup_stage\":%u,\"last_win32_error\":%u}\n",
        phase, Qpc(), static_cast<unsigned>(t.lifecycle), t.capabilities, t.candidateCalls, t.independentFlipCalls,
        t.targetMatches, t.verifiedSdrMatches, t.vetoedCandidates, t.demotedIndependentFlip, t.readFaults, t.activeCallbacks, t.expiredLeaseCalls,
        has ? "true" : "false", sample.qpc, static_cast<unsigned>(sample.stage), static_cast<unsigned>(sample.point), static_cast<unsigned>(sample.decision),
        sample.visual, sample.surface, sample.bind, sample.hwnd, sample.ownerPid, sample.colorSpace, sample.pixelFormat,
        sample.flags, sample.originalEnable, hasLatest ? "true" : "false", static_cast<unsigned>(latest.stage),
        static_cast<unsigned>(latest.point), latest.visual, static_cast<unsigned>(latest.decision),
        latest.hwnd, latest.ownerPid, latest.colorSpace, latest.pixelFormat, latest.flags,
        t.lastCallbackQpc, startupStage, t.lastWin32Error);
    if (!g_managedPipe) std::fflush(stdout);
}
bool StillTarget(HANDLE target, std::uint64_t created, const Request& request) {
    DWORD owner = 0;
    return GetProcessId(target) == request.targetPid && WaitForSingleObject(target, 0) == WAIT_TIMEOUT && CreationTime(target) == created &&
        GetWindowThreadProcessId(reinterpret_cast<HWND>(request.targetHwnd), &owner) != 0 && owner == request.targetPid;
}
}

int wmain(int argc, wchar_t** argv) {
    struct DrainManagedOutput {
        ~DrainManagedOutput() { if (g_managedPipe) g_managedPipe->Drain(); }
    } drainManagedOutput;
    Options options;
    if (!Parse(argc, argv, options)) {
        std::fprintf(stderr, "Usage: --observe|--veto|--inspect-dll --dll ABS_PATH [--target-pid PID --target-hwnd HWND --duration-ms 100..5000] "
            "[--target-exe ABS_LOCAL_PATH --target-creation-filetime FILETIME] "
            "[--expected-dwm-pid PID --expected-dwm-creation-filetime FILETIME] "
            "[--owner-pid PID --owner-creation-filetime FILETIME --pipe NAME [--continuous]] [--validate-only]\n");
        return 2;
    }
    if (options.validateOnly) {
        Emit("{\"event\":\"request_validated\",\"target_pid\":%lu,\"target_hwnd\":%llu,\"duration_ms\":%lu,"
            "\"target_exe\":%s,\"target_creation_filetime\":%llu,\"target_identity_explicitly_bound\":%s,"
            "\"owner_pid\":%lu,\"owner_creation_filetime\":%llu,\"pipe\":%s,\"continuous\":%s,"
            "\"expected_dwm_pid\":%lu,\"expected_dwm_creation_filetime\":%llu,\"process_access\":false,\"dll_loaded\":false}\n",
            options.targetPid, options.targetHwnd, options.durationMs, JsonString(options.targetExe).c_str(),
            options.targetCreation, options.targetExe.empty() ? "false" : "true", options.ownerPid, options.ownerCreation,
            JsonString(options.pipe).c_str(), options.continuous ? "true" : "false", options.expectedDwmPid, options.expectedDwmCreation);
        return 0;
    }
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);
    if (options.ownerPid) {
        g_managedPipe = new ManagedPipe;
        if (!g_managedPipe->Connect(options)) return 23;
        Emit("{\"event\":\"managed_connected\",\"pid\":%lu,\"owner_pid\":%lu,\"continuous\":%s}\n",
            GetCurrentProcessId(), options.ownerPid, options.continuous ? "true" : "false");
        if (!g_managedPipe->WaitForGo()) {
            Emit("{\"event\":\"managed_start_cancelled\",\"remote_writes\":0,\"remote_calls\":0}\n");
            g_managedPipe->Drain(); return 23;
        }
        Emit("{\"event\":\"managed_go\",\"pid\":%lu}\n", GetCurrentProcessId());
    }
    wchar_t fullDll[32768]{};
    const DWORD fullLength = GetFullPathNameW(options.dll.c_str(), 32768, fullDll, nullptr);
    if (!fullLength || fullLength >= 32768) return 3;
    options.dll = fullDll;
    const auto basename = options.dll.substr(options.dll.find_last_of(L"\\/") + 1);
    if (_wcsicmp(basename.c_str(), L"HDRCalibrate.SelectiveDwmHook.dll") != 0) return 3;
    Handle dllLock(CreateFileW(options.dll.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr));
    if (dllLock.value == INVALID_HANDLE_VALUE) return 3;
    const auto dllHash = FileHash(options.dll);
    HMODULE inspected = LoadLibraryExW(options.dll.c_str(), nullptr, DONT_RESOLVE_DLL_REFERENCES);
    if (!inspected) return 3;
    const auto startRva = ExportRva(inspected, "SelectiveDwmStart");
    const auto stopRva = ExportRva(inspected, "SelectiveDwmStop");
    const auto capsRva = ExportRva(inspected, "SelectiveDwmGetCapabilities");
    const auto* localBase = reinterpret_cast<const BYTE*>(inspected);
    const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(localBase);
    const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(localBase + dos->e_lfanew);
    const auto imageSize = nt->OptionalHeader.SizeOfImage;
    const auto imageTimestamp = nt->FileHeader.TimeDateStamp;
    BYTE startBytes[64]{}, stopBytes[64]{};
    if (!startRva || !stopRva || startRva + sizeof(startBytes) > imageSize || stopRva + sizeof(stopBytes) > imageSize) {
        FreeLibrary(inspected); return 3;
    }
    std::memcpy(startBytes, localBase + startRva, sizeof(startBytes));
    std::memcpy(stopBytes, localBase + stopRva, sizeof(stopBytes));
    FreeLibrary(inspected);
    Emit("{\"event\":\"dll_inspected\",\"sha256\":%s,\"start_rva\":%llu,\"stop_rva\":%llu,\"caps_rva\":%llu,\"protocol_size\":%zu}\n",
        JsonString(dllHash).c_str(), startRva, stopRva, capsRva, sizeof(SharedState));
    if (!startRva || !stopRva || !capsRva || dllHash.empty()) return 3;
    if (options.inspectDll) return 0;
    // Bind the selected process object before discovering DWM, enabling debug
    // privilege, or requesting any injection-capable compositor handle. No
    // selected application is ever opened with memory or execution rights.
    constexpr DWORD identityRights = PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE;
    Handle target(OpenProcess(identityRights, FALSE, options.targetPid));
    std::uint64_t targetCreation = 0;
    std::wstring targetImage;
    const DWORD targetError = target.value ? VerifyTargetMetadata(target.value, options, targetCreation, targetImage) : GetLastError();
    if (targetError) {
        Emit("{\"event\":\"expected_target_identity_mismatch\",\"target_pid\":%lu,\"expected_target_exe\":%s,"
            "\"expected_target_creation_filetime\":%llu,\"error\":%lu,\"remote_writes\":0,\"remote_calls\":0}\n",
            options.targetPid, JsonString(options.targetExe).c_str(), options.targetCreation, targetError);
        return 22;
    }
    DWORD targetSession = 0, controllerSession = 0, owner = 0;
    if (!ProcessIdToSessionId(options.targetPid, &targetSession) ||
        !ProcessIdToSessionId(GetCurrentProcessId(), &controllerSession) || targetSession != controllerSession ||
        !GetWindowThreadProcessId(reinterpret_cast<HWND>(options.targetHwnd), &owner) || owner != options.targetPid) {
        Emit("{\"event\":\"target_window_or_session_mismatch\",\"remote_writes\":0,\"remote_calls\":0}\n");
        return 12;
    }
    Emit("{\"event\":\"target_identity_selected\",\"target_pid\":%lu,\"target_exe\":%s,"
        "\"target_creation_filetime\":%llu,\"explicitly_bound\":%s,\"process_rights\":%lu}\n",
        options.targetPid, JsonString(targetImage).c_str(), targetCreation, options.targetExe.empty() ? "false" : "true", identityRights);
    const DWORD dwmPid = FindDwm();
    if (!dwmPid || dwmPid == options.targetPid) return 11;
    if (options.expectedDwmPid && dwmPid != options.expectedDwmPid) {
        Emit("{\"event\":\"expected_dwm_identity_mismatch\",\"actual_dwm_pid\":%lu,\"expected_dwm_pid\":%lu,"
            "\"remote_writes\":0,\"remote_calls\":0}\n", dwmPid, options.expectedDwmPid);
        return 21;
    }
    // Keep a metadata/liveness handle to the expected generation before any
    // injection-capable handle is requested. FILETIME is not .NET DateTime ticks.
    Handle expectedDwm;
    if (options.expectedDwmPid) {
        expectedDwm.value = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, dwmPid);
        if (!expectedDwm.value || WaitForSingleObject(expectedDwm.value, 0) != WAIT_TIMEOUT ||
            CreationTime(expectedDwm.value) != options.expectedDwmCreation) {
            Emit("{\"event\":\"expected_dwm_identity_mismatch\",\"expected_dwm_pid\":%lu,"
                "\"expected_dwm_creation_filetime\":%llu,\"remote_writes\":0,\"remote_calls\":0}\n",
                options.expectedDwmPid, options.expectedDwmCreation);
            return 21;
        }
    }
    if (!ManagedHealthy()) return 23;
    const auto preflight = RunSelectivePreflight();
    if (g_managedPipe) Emit("{\"event\":\"managed_preflight\",\"eligible\":%s}\n", preflight == 0 ? "true" : "false");
    if (preflight != 0) return 10;
    if (!ManagedHealthy()) return 23;
    DebugPrivilege debug;
    Handle dwm(OpenProcess(PROCESS_CREATE_THREAD | PROCESS_DUP_HANDLE | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ | SYNCHRONIZE, FALSE, dwmPid));
    if (!dwm.value) return 11;
    const auto dwmCreation = CreationTime(dwm.value);
    if (!dwmCreation) return 11;
    if (options.expectedDwmPid && (dwmCreation != options.expectedDwmCreation ||
        WaitForSingleObject(expectedDwm.value, 0) != WAIT_TIMEOUT || GetProcessId(dwm.value) != options.expectedDwmPid)) {
        Emit("{\"event\":\"expected_dwm_generation_changed\",\"remote_writes\":0,\"remote_calls\":0}\n");
        return 21;
    }
    Emit("{\"event\":\"dwm_identity_selected\",\"dwm_pid\":%lu,\"dwm_creation_filetime\":%llu,\"explicitly_bound\":%s}\n",
        dwmPid, dwmCreation, options.expectedDwmPid ? "true" : "false");
    DWORD dwmSession = 0;
    if (!ProcessIdToSessionId(dwmPid, &dwmSession) || targetSession != dwmSession ||
        !GetWindowThreadProcessId(reinterpret_cast<HWND>(options.targetHwnd), &owner) || owner != options.targetPid) return 12;
    Request request{};
    request.magic = kProtocolMagic; request.version = kProtocolVersion; request.size = sizeof(Request);
    if (BCryptGenRandom(nullptr, reinterpret_cast<PUCHAR>(&request.nonceLow), 16, BCRYPT_USE_SYSTEM_PREFERRED_RNG) < 0 ||
        (!request.nonceLow && !request.nonceHigh)) return 13;
    request.controllerPid = GetCurrentProcessId(); request.targetPid = options.targetPid;
    request.controllerCreationTime = CreationTime(GetCurrentProcess()); request.targetCreationTime = targetCreation;
    request.targetHwnd = options.targetHwnd; request.qpcFrequency = Frequency(); request.mode = Mode::Observe;
    if (!StillTarget(target.value, targetCreation, request) || !ManagedHealthy()) {
        Emit("{\"event\":\"target_identity_changed_before_activation\",\"remote_writes\":0,\"remote_calls\":0}\n");
        return 22;
    }
    wchar_t mappingName[kMappingNameCharacters]{};
    swprintf_s(mappingName, L"Local\\HDRCalibrate.SelectiveDwm.%lu.%016llX%016llX", GetCurrentProcessId(), request.nonceLow, request.nonceHigh);
    const auto selfSid = SidOf(GetCurrentProcess()), dwmSid = SidOf(dwm.value);
    if (selfSid.empty() || dwmSid.empty()) return 13;
    const auto sddl = L"D:P(A;;GA;;;" + selfSid + L")(A;;GA;;;" + dwmSid + L")(A;;GA;;;SY)";
    PSECURITY_DESCRIPTOR descriptor = nullptr;
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(), SDDL_REVISION_1, &descriptor, nullptr)) return 13;
    SECURITY_ATTRIBUTES security{ sizeof(security), descriptor, FALSE };
    Handle mapping(CreateFileMappingW(INVALID_HANDLE_VALUE, &security, PAGE_READWRITE, 0, sizeof(SharedState), mappingName));
    const auto mappingError = GetLastError(); LocalFree(descriptor);
    if (!mapping.value || mappingError == ERROR_ALREADY_EXISTS) return 13;
    auto* shared = static_cast<SharedState*>(MapViewOfFile(mapping.value, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(SharedState)));
    if (!shared) return 13;
    std::memset(shared, 0, sizeof(*shared));
    // Load the reviewed library before issuing the bounded observer lease.
    Module conflictingModule;
    const auto moduleCheck = CheckExclusiveHookPath(dwmPid, options.dll, conflictingModule);
    if (moduleCheck) {
        Emit("{\"event\":\"conflicting_hook_module\",\"existing_path\":%s,\"error\":%lu,"
            "\"remote_writes\":0,\"remote_calls\":0,\"requires_fresh_dwm_generation\":true}\n",
            JsonString(conflictingModule.path).c_str(), moduleCheck);
        UnmapViewOfFile(shared); return 24;
    }
    auto module = FindModule(dwmPid, options.dll);
    if (!StillTarget(target.value, targetCreation, request) || !ManagedHealthy()) {
        Emit("{\"event\":\"target_identity_changed_before_activation\",\"remote_writes\":0,\"remote_calls\":0}\n");
        UnmapViewOfFile(shared); return 22;
    }
    if (!module.base) {
        const auto load = RemoteEquivalent(dwmPid, GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "LoadLibraryW"));
        if (!load) { UnmapViewOfFile(shared); return 14; }
        const auto call = RemoteCall(dwm.value, load, options.dll.c_str(), (options.dll.size() + 1) * sizeof(wchar_t));
        if (!call.completed) { UnmapViewOfFile(shared); return 14; }
        module = FindModule(dwmPid, options.dll);
    }
    if (!module.base || startRva >= module.size || stopRva >= module.size) { UnmapViewOfFile(shared); return 14; }
    // Path equality is insufficient for an already pinned module. Compare PE
    // identity and the exact, unhooked export entries before using local RVAs.
    IMAGE_DOS_HEADER remoteDos{}; IMAGE_NT_HEADERS64 remoteNt{};
    BYTE remoteStart[64]{}, remoteStop[64]{}; SIZE_T checked = 0;
    const auto readExact = [&](std::uint64_t address, void* data, SIZE_T length) {
        return ReadProcessMemory(dwm.value, reinterpret_cast<LPCVOID>(address), data, length, &checked) && checked == length;
    };
    if (module.size != imageSize || !readExact(module.base, &remoteDos, sizeof(remoteDos)) || remoteDos.e_magic != IMAGE_DOS_SIGNATURE ||
        remoteDos.e_lfanew < 0 || static_cast<DWORD>(remoteDos.e_lfanew) > module.size - sizeof(remoteNt) ||
        !readExact(module.base + remoteDos.e_lfanew, &remoteNt, sizeof(remoteNt)) || remoteNt.Signature != IMAGE_NT_SIGNATURE ||
        remoteNt.FileHeader.TimeDateStamp != imageTimestamp || remoteNt.OptionalHeader.SizeOfImage != imageSize ||
        !readExact(module.base + startRva, remoteStart, sizeof(remoteStart)) || !readExact(module.base + stopRva, remoteStop, sizeof(remoteStop)) ||
        std::memcmp(startBytes, remoteStart, sizeof(startBytes)) || std::memcmp(stopBytes, remoteStop, sizeof(stopBytes))) {
        Emit("{\"event\":\"loaded_module_identity_mismatch\",\"exports_not_called\":true}\n"); UnmapViewOfFile(shared); return 14;
    }
    StartConfig config{}; config.size = sizeof(config); config.version = kProtocolVersion; config.magic = kProtocolMagic;
    config.nonceLow = request.nonceLow; config.nonceHigh = request.nonceHigh;
    HANDLE borrowedController = nullptr, borrowedTarget = nullptr;
    const auto closeBorrowed = [&](HANDLE value) {
        if (!value) return true;
        HANDLE localCopy = nullptr;
        // DUPLICATE_CLOSE_SOURCE is attempted once only: its source-close can
        // occur even when duplication fails, so retrying could close a reused ID.
        const BOOL closed = DuplicateHandle(dwm.value, value, GetCurrentProcess(), &localCopy, 0, FALSE,
            DUPLICATE_CLOSE_SOURCE | DUPLICATE_SAME_ACCESS);
        if (localCopy) CloseHandle(localCopy);
        return closed != FALSE;
    };
    if (!StillTarget(target.value, targetCreation, request) || !ManagedHealthy()) {
        Emit("{\"event\":\"target_identity_changed_before_start\",\"start_not_called\":true}\n");
        UnmapViewOfFile(shared); return 22;
    }
    if (!DuplicateHandle(GetCurrentProcess(), GetCurrentProcess(), dwm.value, &borrowedController, identityRights, FALSE, 0) ||
        !DuplicateHandle(GetCurrentProcess(), target.value, dwm.value, &borrowedTarget, identityRights, FALSE, 0)) {
        const auto error = GetLastError();
        closeBorrowed(borrowedController); closeBorrowed(borrowedTarget);
        Emit("{\"event\":\"readonly_handle_transfer_failed\",\"error\":%lu}\n", error);
        UnmapViewOfFile(shared); return 15;
    }
    config.controllerProcessHandle = reinterpret_cast<std::uint64_t>(borrowedController);
    config.targetProcessHandle = reinterpret_cast<std::uint64_t>(borrowedTarget);
    std::memcpy(config.mappingName, mappingName, (wcslen(mappingName) + 1) * sizeof(wchar_t));
    if (!StillTarget(target.value, targetCreation, request) || !ManagedHealthy()) {
        closeBorrowed(borrowedController); closeBorrowed(borrowedTarget);
        Emit("{\"event\":\"target_identity_changed_before_start\",\"start_not_called\":true}\n");
        UnmapViewOfFile(shared); return 22;
    }
    request.leaseIssuedQpc = Qpc(); request.leaseDeadlineQpc = request.leaseIssuedQpc + request.qpcFrequency * options.durationMs / 1000;
    Publish(shared, request);
    const auto start = RemoteCall(dwm.value, module.base + startRva, &config, sizeof(config));
    bool borrowedClosed = false;
    if (start.completed) {
        const bool controllerClosed = closeBorrowed(borrowedController);
        const bool targetClosed = closeBorrowed(borrowedTarget);
        borrowedClosed = controllerClosed && targetClosed;
    }
    bool managedAborted = !ManagedHealthy();
    if (managedAborted) {
        request.mode = Mode::Observe; request.leaseDeadlineQpc = request.leaseIssuedQpc;
        Publish(shared, request);
    }
    Emit("{\"event\":\"borrowed_identity_handles\",\"rights\":%lu,\"closed_after_start\":%s,\"retained_for_incomplete_start\":%s}\n",
        identityRights, borrowedClosed ? "true" : "false", start.completed ? "false" : "true");
    Emit("{\"event\":\"start\",\"completed\":%s,\"status\":%lu,\"error\":%lu,\"dwm_pid\":%lu,\"target_pid\":%lu,\"qpc_frequency\":%lld,\"issued_qpc\":%lld,\"deadline_qpc\":%lld}\n",
        start.completed ? "true" : "false", start.result, start.error, dwmPid, options.targetPid,
        request.qpcFrequency, request.leaseIssuedQpc, request.leaseDeadlineQpc);
    bool started = start.completed && start.result == ERROR_SUCCESS;
    bool armed = false;
    bool targetOk = true;
    bool surfaceRejected = false;
    Sample rejectedSurface{};
    std::int64_t armedQpc = 0;
    auto nextLog = Qpc();
    while (started && !managedAborted && Qpc() < request.leaseDeadlineQpc && WaitForSingleObject(dwm.value, 0) == WAIT_TIMEOUT) {
        if (!ManagedHealthy()) { managedAborted = true; break; }
        if (!StillTarget(target.value, targetCreation, request)) { targetOk = false; break; }
        Sample sample{};
        const bool hasSample = SampleSnapshot(shared->telemetry.latestTarget, sample);
        if (options.continuous && armed && hasSample && ManagedSurfaceRejected(request, sample, Qpc(), armedQpc)) {
            surfaceRejected = true; rejectedSurface = sample; break;
        }
        if (options.veto && !armed && hasSample &&
            sample.qpc >= request.leaseIssuedQpc && Qpc() - sample.qpc < request.qpcFrequency &&
            sample.point == HookPoint::Candidate && sample.stage == Stage::Complete && sample.decision == Decision::Observe &&
            (sample.flags & ObservedBeforeArm) &&
            (sample.flags & HasBindInfo) && sample.bind &&
            (sample.flags & OwnershipVerified) && (sample.flags & HasPixelFormat) &&
            sample.hwnd == request.targetHwnd && sample.ownerPid == request.targetPid &&
            IsVerifiedSdrFormat(sample.colorSpace, sample.pixelFormat) && sample.visual && sample.surface &&
            (shared->telemetry.capabilities & VetoCandidates)) {
            request.expectedVisual = sample.visual; request.expectedSurface = sample.surface;
            request.expectedBind = sample.bind;
            request.mode = Mode::CandidateVeto; request.leaseIssuedQpc = Qpc();
            request.leaseDeadlineQpc = request.leaseIssuedQpc + request.qpcFrequency * options.durationMs / 1000;
            Publish(shared, request); armed = true; armedQpc = request.leaseIssuedQpc;
            Emit("{\"event\":\"veto_armed\",\"qpc\":%lld,\"deadline_qpc\":%lld,\"visual\":%llu,\"surface\":%llu}\n",
                request.leaseIssuedQpc, request.leaseDeadlineQpc, sample.visual, sample.surface);
        }
        if (options.continuous && Qpc() >= request.leaseIssuedQpc + request.qpcFrequency * options.durationMs / 2000) {
            // Renew only this immutable session and its already selected
            // surface tuple. Every published lease has its own <=5s deadline.
            // If a polling turn missed the old deadline, do not resurrect it.
            const auto now = Qpc();
            if (now >= request.leaseDeadlineQpc) break;
            if (!ManagedHealthy()) { managedAborted = true; break; }
            if (!StillTarget(target.value, targetCreation, request)) { targetOk = false; break; }
            if (WaitForSingleObject(dwm.value, 0) != WAIT_TIMEOUT || CreationTime(dwm.value) != dwmCreation) break;
            if (!RenewManagedLease(request, now, options.durationMs)) break;
            Publish(shared, request);
            Emit("{\"event\":\"lease_renewed\",\"qpc\":%lld,\"deadline_qpc\":%lld,\"mode\":%u}\n",
                request.leaseIssuedQpc, request.leaseDeadlineQpc, static_cast<unsigned int>(request.mode));
        }
        if (Qpc() >= nextLog) { PrintTelemetry(shared, armed ? "veto" : "observe"); nextLog = Qpc() + request.qpcFrequency / 4; }
        Sleep(10);
    }
    // On normal expiry deliberately leave the request alone: callbacks must
    // independently observe their deadline. An abort explicitly revokes it.
    if (!started || !targetOk || managedAborted || surfaceRejected || options.continuous) {
        request.mode = Mode::Observe; request.leaseDeadlineQpc = request.leaseIssuedQpc;
        Publish(shared, request);
    }
    // Cancellation precedes every stop/status write. Managed Stop invokes the
    // pinned export immediately; no stdout or pipe reader can delay revocation.
    RemoteCallResult stopped;
    if ((managedAborted || surfaceRejected) && started && WaitForSingleObject(dwm.value, 0) == WAIT_TIMEOUT)
        stopped = RemoteCall(dwm.value, module.base + stopRva, nullptr, 0);
    if (surfaceRejected) Emit("{\"event\":\"managed_surface_rejected\",\"decision\":%u,\"qpc\":%lld,"
        "\"target_pid\":%lu,\"target_hwnd\":%llu,\"colorspace\":%u,\"format\":%u,\"lease_revoked\":true,\"relearned\":false}\n",
        static_cast<unsigned int>(rejectedSurface.decision), rejectedSurface.qpc, options.targetPid, options.targetHwnd,
        rejectedSurface.colorSpace, rejectedSurface.pixelFormat);
    const auto expiredVetoes = shared->telemetry.vetoedCandidates;
    const auto expiryObservationEnd = Qpc() + request.qpcFrequency / 3;
    while (Qpc() < expiryObservationEnd) Sleep(10);
    const bool noPostExpiryVeto = shared->telemetry.vetoedCandidates == expiredVetoes;
    PrintTelemetry(shared, "expired");
    if (!managedAborted && !surfaceRejected && started && WaitForSingleObject(dwm.value, 0) == WAIT_TIMEOUT)
        stopped = RemoteCall(dwm.value, module.base + stopRva, nullptr, 0);
    const auto recoveryDeadline = Qpc() + request.qpcFrequency;
    while (Qpc() < recoveryDeadline) Sleep(10);
    PrintTelemetry(shared, "recovery");
    const bool retainedDwm = WaitForSingleObject(dwm.value, 0) == WAIT_TIMEOUT && CreationTime(dwm.value) == dwmCreation;
    const bool retainedTarget = StillTarget(target.value, targetCreation, request);
    const auto vetoes = shared->telemetry.vetoedCandidates;
    const auto faults = shared->telemetry.readFaults;
    const auto remainingCallbacks = shared->telemetry.activeCallbacks;
    Emit("{\"event\":\"complete\",\"started\":%s,\"armed\":%s,\"target_identity_retained\":%s,\"dwm_identity_retained\":%s,"
        "\"stop_completed\":%s,\"stop_status\":%lu,\"vetoed_candidates\":%lld,\"read_faults\":%lld,\"active_callbacks\":%lld,\"no_post_expiry_veto\":%s,"
        "\"managed\":%s,\"continuous\":%s,\"stop_requested\":%s,\"managed_error\":%lu,\"surface_rejected\":%s,"
        "\"dll_loaded_retained\":true,\"hooks_available_for_session\":%s,\"game_writes\":0,\"gamma_applied\":false,\"composition_confirmed\":false,\"vrr_confirmed\":false}\n",
        started ? "true" : "false", armed ? "true" : "false", retainedTarget && targetOk ? "true" : "false", retainedDwm ? "true" : "false",
        stopped.completed ? "true" : "false", stopped.result, vetoes, faults, remainingCallbacks, noPostExpiryVeto ? "true" : "false",
        g_managedPipe ? "true" : "false", options.continuous ? "true" : "false",
        g_managedPipe && g_managedPipe->StopRequested() ? "true" : "false", g_managedPipe ? g_managedPipe->Error() : 0,
        surfaceRejected ? "true" : "false",
        started ? "true" : "false");
    UnmapViewOfFile(shared);
    return started && stopped.completed && stopped.result == ERROR_SUCCESS && retainedDwm && retainedTarget && targetOk &&
        faults == 0 && remainingCallbacks == 0 && borrowedClosed && noPostExpiryVeto && !surfaceRejected &&
        (!managedAborted || (g_managedPipe && g_managedPipe->StopRequested())) &&
        (!options.continuous || (g_managedPipe && g_managedPipe->StopRequested())) &&
        (!options.veto || (armed && vetoes > 0)) ? 0 : 20;
}
