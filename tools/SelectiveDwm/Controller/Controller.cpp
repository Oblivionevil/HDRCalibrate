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
    DWORD durationMs = 5000;
    std::wstring dll;
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
        } else if (arg == L"--dll") {
            if (++i == argc || !result.dll.empty()) return false; result.dll = argv[i];
        } else if (arg == L"--target-pid" || arg == L"--target-hwnd" || arg == L"--duration-ms" ||
            arg == L"--expected-dwm-pid" || arg == L"--expected-dwm-creation-filetime") {
            if (++i == argc) return false;
            std::uint64_t value = 0;
            const std::uint64_t maximum = arg == L"--target-hwnd" ? UINT64_MAX :
                arg == L"--expected-dwm-creation-filetime" ? static_cast<std::uint64_t>(INT64_MAX) : MAXDWORD;
            if (!Number(argv[i], maximum, value) || !value) return false;
            if (arg == L"--target-pid") { if (result.targetPid) return false; result.targetPid = static_cast<DWORD>(value); }
            if (arg == L"--target-hwnd") { if (result.targetHwnd) return false; result.targetHwnd = value; }
            if (arg == L"--expected-dwm-pid") {
                if (result.expectedDwmPid) return false; result.expectedDwmPid = static_cast<DWORD>(value);
            }
            if (arg == L"--expected-dwm-creation-filetime") {
                if (result.expectedDwmCreation) return false; result.expectedDwmCreation = value;
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
        !(result.inspectDll && result.expectedDwmPid);
}
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
        std::printf("{\"event\":\"remote_call_incomplete\",\"error\":%lu,\"argument_retained\":%s}\n", result.error, remote ? "true" : "false");
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
    std::printf("{\"event\":\"telemetry\",\"phase\":\"%s\",\"qpc\":%lld,\"lifecycle\":%u,\"capabilities\":%u,"
        "\"candidate_calls\":%lld,\"iflip_calls\":%lld,\"target_matches\":%lld,\"sdr_matches\":%lld,"
        "\"vetoed\":%lld,\"demoted\":%lld,\"read_faults\":%lld,\"active_callbacks\":%lld,"
        "\"sample_valid\":%s,\"sample_qpc\":%lld,\"point\":%u,\"decision\":%u,\"visual\":%llu,\"surface\":%llu,"
        "\"bind\":%llu,\"hwnd\":%llu,\"owner_pid\":%u,\"colorspace\":%u,\"format\":%u,\"flags\":%u,\"original_enable\":%u,"
        "\"latest_valid\":%s,\"latest_stage\":%u,\"latest_point\":%u,\"latest_visual\":%llu,\"latest_decision\":%u,"
        "\"latest_hwnd\":%llu,\"latest_owner_pid\":%u,\"latest_colorspace\":%u,\"latest_format\":%u,\"latest_flags\":%u,"
        "\"last_callback_qpc\":%lld,\"startup_stage\":%u,\"last_win32_error\":%u}\n",
        phase, Qpc(), static_cast<unsigned>(t.lifecycle), t.capabilities, t.candidateCalls, t.independentFlipCalls,
        t.targetMatches, t.verifiedSdrMatches, t.vetoedCandidates, t.demotedIndependentFlip, t.readFaults, t.activeCallbacks,
        has ? "true" : "false", sample.qpc, static_cast<unsigned>(sample.point), static_cast<unsigned>(sample.decision),
        sample.visual, sample.surface, sample.bind, sample.hwnd, sample.ownerPid, sample.colorSpace, sample.pixelFormat,
        sample.flags, sample.originalEnable, hasLatest ? "true" : "false", static_cast<unsigned>(latest.stage),
        static_cast<unsigned>(latest.point), latest.visual, static_cast<unsigned>(latest.decision),
        latest.hwnd, latest.ownerPid, latest.colorSpace, latest.pixelFormat, latest.flags,
        t.lastCallbackQpc, startupStage, t.lastWin32Error);
    std::fflush(stdout);
}
bool StillTarget(HANDLE target, std::uint64_t created, const Request& request) {
    DWORD owner = 0;
    return WaitForSingleObject(target, 0) == WAIT_TIMEOUT && CreationTime(target) == created &&
        GetWindowThreadProcessId(reinterpret_cast<HWND>(request.targetHwnd), &owner) != 0 && owner == request.targetPid;
}
}

int wmain(int argc, wchar_t** argv) {
    Options options;
    if (!Parse(argc, argv, options)) {
        std::fprintf(stderr, "Usage: --observe|--veto|--inspect-dll --dll ABS_PATH [--target-pid PID --target-hwnd HWND --duration-ms 100..5000] "
            "[--expected-dwm-pid PID --expected-dwm-creation-filetime FILETIME] [--validate-only]\n");
        return 2;
    }
    if (options.validateOnly) {
        std::printf("{\"event\":\"request_validated\",\"target_pid\":%lu,\"target_hwnd\":%llu,\"duration_ms\":%lu,"
            "\"expected_dwm_pid\":%lu,\"expected_dwm_creation_filetime\":%llu,\"process_access\":false,\"dll_loaded\":false}\n",
            options.targetPid, options.targetHwnd, options.durationMs, options.expectedDwmPid, options.expectedDwmCreation);
        return 0;
    }
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);
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
    std::wprintf(L"{\"event\":\"dll_inspected\",\"sha256\":\"%ls\",\"start_rva\":%llu,\"stop_rva\":%llu,\"caps_rva\":%llu,\"protocol_size\":%zu}\n",
        dllHash.c_str(), startRva, stopRva, capsRva, sizeof(SharedState));
    if (!startRva || !stopRva || !capsRva || dllHash.empty()) return 3;
    if (options.inspectDll) return 0;
    const DWORD dwmPid = FindDwm();
    if (!dwmPid || dwmPid == options.targetPid) return 11;
    if (options.expectedDwmPid && dwmPid != options.expectedDwmPid) {
        std::printf("{\"event\":\"expected_dwm_identity_mismatch\",\"actual_dwm_pid\":%lu,\"expected_dwm_pid\":%lu,"
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
            std::printf("{\"event\":\"expected_dwm_identity_mismatch\",\"expected_dwm_pid\":%lu,"
                "\"expected_dwm_creation_filetime\":%llu,\"remote_writes\":0,\"remote_calls\":0}\n",
                options.expectedDwmPid, options.expectedDwmCreation);
            return 21;
        }
    }
    if (RunSelectivePreflight() != 0) return 10;
    DebugPrivilege debug;
    Handle dwm(OpenProcess(PROCESS_CREATE_THREAD | PROCESS_DUP_HANDLE | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ | SYNCHRONIZE, FALSE, dwmPid));
    Handle target(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, options.targetPid));
    if (!dwm.value || !target.value) return 11;
    const auto dwmCreation = CreationTime(dwm.value);
    const auto targetCreation = CreationTime(target.value);
    if (!dwmCreation || !targetCreation) return 11;
    if (options.expectedDwmPid && (dwmCreation != options.expectedDwmCreation ||
        WaitForSingleObject(expectedDwm.value, 0) != WAIT_TIMEOUT || GetProcessId(dwm.value) != options.expectedDwmPid)) {
        std::printf("{\"event\":\"expected_dwm_generation_changed\",\"remote_writes\":0,\"remote_calls\":0}\n");
        return 21;
    }
    std::printf("{\"event\":\"dwm_identity_selected\",\"dwm_pid\":%lu,\"dwm_creation_filetime\":%llu,\"explicitly_bound\":%s}\n",
        dwmPid, dwmCreation, options.expectedDwmPid ? "true" : "false");
    DWORD targetSession = 0, dwmSession = 0, owner = 0;
    if (!ProcessIdToSessionId(options.targetPid, &targetSession) || !ProcessIdToSessionId(dwmPid, &dwmSession) || targetSession != dwmSession ||
        !GetWindowThreadProcessId(reinterpret_cast<HWND>(options.targetHwnd), &owner) || owner != options.targetPid) return 12;
    Request request{};
    request.magic = kProtocolMagic; request.version = kProtocolVersion; request.size = sizeof(Request);
    if (BCryptGenRandom(nullptr, reinterpret_cast<PUCHAR>(&request.nonceLow), 16, BCRYPT_USE_SYSTEM_PREFERRED_RNG) < 0 ||
        (!request.nonceLow && !request.nonceHigh)) return 13;
    request.controllerPid = GetCurrentProcessId(); request.targetPid = options.targetPid;
    request.controllerCreationTime = CreationTime(GetCurrentProcess()); request.targetCreationTime = targetCreation;
    request.targetHwnd = options.targetHwnd; request.qpcFrequency = Frequency(); request.mode = Mode::Observe;
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
    auto module = FindModule(dwmPid, options.dll);
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
        std::printf("{\"event\":\"loaded_module_identity_mismatch\",\"exports_not_called\":true}\n"); UnmapViewOfFile(shared); return 14;
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
    constexpr DWORD identityRights = PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE;
    if (!DuplicateHandle(GetCurrentProcess(), GetCurrentProcess(), dwm.value, &borrowedController, identityRights, FALSE, 0) ||
        !DuplicateHandle(GetCurrentProcess(), target.value, dwm.value, &borrowedTarget, identityRights, FALSE, 0)) {
        const auto error = GetLastError();
        closeBorrowed(borrowedController); closeBorrowed(borrowedTarget);
        std::printf("{\"event\":\"readonly_handle_transfer_failed\",\"error\":%lu}\n", error);
        UnmapViewOfFile(shared); return 15;
    }
    config.controllerProcessHandle = reinterpret_cast<std::uint64_t>(borrowedController);
    config.targetProcessHandle = reinterpret_cast<std::uint64_t>(borrowedTarget);
    std::memcpy(config.mappingName, mappingName, (wcslen(mappingName) + 1) * sizeof(wchar_t));
    request.leaseIssuedQpc = Qpc(); request.leaseDeadlineQpc = request.leaseIssuedQpc + request.qpcFrequency * options.durationMs / 1000;
    Publish(shared, request);
    const auto start = RemoteCall(dwm.value, module.base + startRva, &config, sizeof(config));
    bool borrowedClosed = false;
    if (start.completed) {
        const bool controllerClosed = closeBorrowed(borrowedController);
        const bool targetClosed = closeBorrowed(borrowedTarget);
        borrowedClosed = controllerClosed && targetClosed;
    }
    std::printf("{\"event\":\"borrowed_identity_handles\",\"rights\":%lu,\"closed_after_start\":%s,\"retained_for_incomplete_start\":%s}\n",
        identityRights, borrowedClosed ? "true" : "false", start.completed ? "false" : "true");
    std::printf("{\"event\":\"start\",\"completed\":%s,\"status\":%lu,\"error\":%lu,\"dwm_pid\":%lu,\"target_pid\":%lu,\"qpc_frequency\":%lld,\"issued_qpc\":%lld,\"deadline_qpc\":%lld}\n",
        start.completed ? "true" : "false", start.result, start.error, dwmPid, options.targetPid,
        request.qpcFrequency, request.leaseIssuedQpc, request.leaseDeadlineQpc);
    bool started = start.completed && start.result == ERROR_SUCCESS;
    bool armed = false;
    bool targetOk = true;
    auto nextLog = Qpc();
    while (started && Qpc() < request.leaseDeadlineQpc && WaitForSingleObject(dwm.value, 0) == WAIT_TIMEOUT) {
        if (!StillTarget(target.value, targetCreation, request)) { targetOk = false; break; }
        Sample sample{};
        if (options.veto && !armed && SampleSnapshot(shared->telemetry.latestTarget, sample) &&
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
            Publish(shared, request); armed = true;
            std::printf("{\"event\":\"veto_armed\",\"qpc\":%lld,\"deadline_qpc\":%lld,\"visual\":%llu,\"surface\":%llu}\n",
                request.leaseIssuedQpc, request.leaseDeadlineQpc, sample.visual, sample.surface);
        }
        if (Qpc() >= nextLog) { PrintTelemetry(shared, armed ? "veto" : "observe"); nextLog = Qpc() + request.qpcFrequency / 4; }
        Sleep(10);
    }
    // On normal expiry deliberately leave the request alone: callbacks must
    // independently observe their deadline. An abort explicitly revokes it.
    if (!started || !targetOk) {
        request.mode = Mode::Observe; request.leaseDeadlineQpc = request.leaseIssuedQpc;
        Publish(shared, request);
    }
    const auto expiredVetoes = shared->telemetry.vetoedCandidates;
    const auto expiryObservationEnd = Qpc() + request.qpcFrequency / 3;
    while (Qpc() < expiryObservationEnd) Sleep(10);
    const bool noPostExpiryVeto = shared->telemetry.vetoedCandidates == expiredVetoes;
    PrintTelemetry(shared, "expired");
    RemoteCallResult stopped;
    if (started && WaitForSingleObject(dwm.value, 0) == WAIT_TIMEOUT)
        stopped = RemoteCall(dwm.value, module.base + stopRva, nullptr, 0);
    const auto recoveryDeadline = Qpc() + request.qpcFrequency;
    while (Qpc() < recoveryDeadline) Sleep(10);
    PrintTelemetry(shared, "recovery");
    const bool retainedDwm = WaitForSingleObject(dwm.value, 0) == WAIT_TIMEOUT && CreationTime(dwm.value) == dwmCreation;
    const bool retainedTarget = StillTarget(target.value, targetCreation, request);
    const auto vetoes = shared->telemetry.vetoedCandidates;
    const auto faults = shared->telemetry.readFaults;
    const auto remainingCallbacks = shared->telemetry.activeCallbacks;
    std::printf("{\"event\":\"complete\",\"started\":%s,\"armed\":%s,\"target_identity_retained\":%s,\"dwm_identity_retained\":%s,"
        "\"stop_completed\":%s,\"stop_status\":%lu,\"vetoed_candidates\":%lld,\"read_faults\":%lld,\"active_callbacks\":%lld,\"no_post_expiry_veto\":%s,"
        "\"dll_loaded_retained\":true,\"hooks_available_for_session\":%s,\"game_writes\":0,\"gamma_applied\":false,\"composition_confirmed\":false,\"vrr_confirmed\":false}\n",
        started ? "true" : "false", armed ? "true" : "false", retainedTarget && targetOk ? "true" : "false", retainedDwm ? "true" : "false",
        stopped.completed ? "true" : "false", stopped.result, vetoes, faults, remainingCallbacks, noPostExpiryVeto ? "true" : "false", started ? "true" : "false");
    UnmapViewOfFile(shared);
    return started && stopped.completed && stopped.result == ERROR_SUCCESS && retainedDwm && retainedTarget && targetOk &&
        faults == 0 && remainingCallbacks == 0 && borrowedClosed && noPostExpiryVeto && (!options.veto || (armed && vetoes > 0)) ? 0 : 20;
}
