// One explicitly requested DISPLAY1 HDR cycle. Only public SDK 26100 APIs.
// The restore child has an inherited handle to THIS controller, never a PID lookup.
#define _WIN32_WINNT 0x0A00
#define NTDDI_VERSION 0x0A000010
#define NOMINMAX
#include <windows.h>
#include <algorithm>
#include <cerrno>
#include <cstdint>
#include <cstdio>
#include <cwchar>
#include <sstream>
#include <string>
#include <vector>

static_assert(DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2 == 15);
static_assert(DISPLAYCONFIG_DEVICE_INFO_SET_HDR_STATE == 16);
static_assert(sizeof(DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2) == 36);
static_assert(sizeof(DISPLAYCONFIG_SET_HDR_STATE) == 24);
static_assert(DISPLAYCONFIG_ADVANCED_COLOR_MODE_HDR == 2);

namespace {
constexpr wchar_t kDisplay[] = L"\\\\.\\DISPLAY1";
struct Handle {
    HANDLE h = nullptr;
    Handle() = default;
    explicit Handle(HANDLE x) : h(x) {}
    ~Handle() { if (h && h != INVALID_HANDLE_VALUE) CloseHandle(h); }
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
};
struct Snapshot {
    wchar_t gdi[32]{};
    wchar_t monitor[128]{};
    LUID sourceAdapter{};
    LUID targetAdapter{};
    UINT32 sourceId = 0, targetId = 0, connector = 0;
    DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2 color{};
};
std::string Json(const wchar_t* value) {
    std::ostringstream out;
    out << '"';
    for (; *value; ++value) {
        const unsigned ch = static_cast<unsigned>(*value);
        if (ch == '"' || ch == '\\') out << '\\' << static_cast<char>(ch);
        else if (ch >= 32 && ch < 127) out << static_cast<char>(ch);
        else { char hex[7]{}; sprintf_s(hex, "\\u%04x", ch); out << hex; }
    }
    out << '"';
    return out.str();
}
void Log(const char* event, const std::string& fields = "") {
    LARGE_INTEGER qpc{};
    QueryPerformanceCounter(&qpc);
    const std::string line = "{\"event\":\"" + std::string(event) + "\",\"pid\":" +
        std::to_string(GetCurrentProcessId()) + ",\"qpc\":" + std::to_string(qpc.QuadPart) + fields + "}\r\n";
    DWORD written = 0;
    WriteFile(GetStdHandle(STD_OUTPUT_HANDLE), line.data(), static_cast<DWORD>(line.size()), &written, nullptr);
}
bool SameLuid(LUID a, LUID b) { return a.LowPart == b.LowPart && a.HighPart == b.HighPart; }
bool SameTarget(const Snapshot& a, const Snapshot& b) {
    return _wcsicmp(a.gdi, b.gdi) == 0 && _wcsicmp(a.monitor, b.monitor) == 0 &&
        SameLuid(a.sourceAdapter, b.sourceAdapter) && SameLuid(a.targetAdapter, b.targetAdapter) &&
        a.sourceId == b.sourceId && a.targetId == b.targetId && a.connector == b.connector;
}
bool HdrReady(const Snapshot& s) {
    return (s.color.value & 0xFFFFFF04u) == 0 && s.color.advancedColorSupported && s.color.advancedColorActive &&
        s.color.highDynamicRangeSupported && s.color.highDynamicRangeUserEnabled &&
        !s.color.advancedColorLimitedByPolicy && s.color.activeColorMode == DISPLAYCONFIG_ADVANCED_COLOR_MODE_HDR;
}
bool Restored(const Snapshot& initial, const Snapshot& current) {
    return SameTarget(initial, current) && HdrReady(current) &&
        initial.color.wideColorUserEnabled == current.color.wideColorUserEnabled;
}
// No fallback to legacy Advanced Color, another monitor, or an inactive path.
LONG ReadDisplay(Snapshot& result) {
    for (unsigned attempt = 0; attempt != 3; ++attempt) {
        UINT32 pathCount = 0, modeCount = 0;
        LONG rc = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, &pathCount, &modeCount);
        if (rc != ERROR_SUCCESS) return rc;
        if (pathCount > 256 || modeCount > 1024) return ERROR_INVALID_DATA;
        std::vector<DISPLAYCONFIG_PATH_INFO> paths(pathCount);
        std::vector<DISPLAYCONFIG_MODE_INFO> modes(modeCount);
        rc = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, &pathCount, paths.data(), &modeCount, modes.data(), nullptr);
        if (rc == ERROR_INSUFFICIENT_BUFFER) continue;
        if (rc != ERROR_SUCCESS) return rc;
        unsigned matches = 0;
        Snapshot candidate{};
        for (UINT32 i = 0; i < pathCount; ++i) {
            const auto& p = paths[i];
            DISPLAYCONFIG_SOURCE_DEVICE_NAME source{};
            source.header = { DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME, sizeof(source), p.sourceInfo.adapterId, p.sourceInfo.id };
            rc = DisplayConfigGetDeviceInfo(&source.header);
            if (rc != ERROR_SUCCESS) return rc; // Cannot establish uniqueness.
            if (_wcsicmp(source.viewGdiDeviceName, kDisplay) != 0) continue;
            if (++matches != 1) return ERROR_DUP_NAME;
            DISPLAYCONFIG_TARGET_DEVICE_NAME target{};
            target.header = { DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME, sizeof(target), p.targetInfo.adapterId, p.targetInfo.id };
            rc = DisplayConfigGetDeviceInfo(&target.header);
            if (rc != ERROR_SUCCESS) return rc;
            if (!target.monitorDevicePath[0]) return ERROR_INVALID_DATA;
            wcscpy_s(candidate.gdi, source.viewGdiDeviceName);
            wcscpy_s(candidate.monitor, target.monitorDevicePath);
            candidate.sourceAdapter = p.sourceInfo.adapterId;
            candidate.sourceId = p.sourceInfo.id;
            candidate.targetAdapter = p.targetInfo.adapterId;
            candidate.targetId = p.targetInfo.id;
            candidate.connector = target.connectorInstance;
            candidate.color.header = { DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2,
                sizeof(candidate.color), p.targetInfo.adapterId, p.targetInfo.id };
            rc = DisplayConfigGetDeviceInfo(&candidate.color.header);
            if (rc != ERROR_SUCCESS) return rc;
        }
        if (matches != 1) return ERROR_NOT_FOUND;
        result = candidate;
        return ERROR_SUCCESS;
    }
    return ERROR_RETRY;
}
void LogSnapshot(const char* event, const Snapshot& s) {
    Log(event, ",\"gdi\":" + Json(s.gdi) + ",\"monitor\":" + Json(s.monitor) +
        ",\"source_luid_low\":" + std::to_string(s.sourceAdapter.LowPart) +
        ",\"source_luid_high\":" + std::to_string(s.sourceAdapter.HighPart) +
        ",\"source_id\":" + std::to_string(s.sourceId) +
        ",\"target_luid_low\":" + std::to_string(s.targetAdapter.LowPart) +
        ",\"target_luid_high\":" + std::to_string(s.targetAdapter.HighPart) +
        ",\"target_id\":" + std::to_string(s.targetId) +
        ",\"connector\":" + std::to_string(s.connector) + ",\"color_flags\":" + std::to_string(s.color.value) +
        ",\"bits_per_channel\":" + std::to_string(s.color.bitsPerColorChannel) +
        ",\"active_color_mode\":" + std::to_string(s.color.activeColorMode) +
        ",\"hdr_supported\":" + std::to_string(s.color.highDynamicRangeSupported) +
        ",\"hdr_user_enabled\":" + std::to_string(s.color.highDynamicRangeUserEnabled) +
        ",\"advanced_color_active\":" + std::to_string(s.color.advancedColorActive) +
        ",\"limited_by_policy\":" + std::to_string(s.color.advancedColorLimitedByPolicy) +
        ",\"wcg_user_enabled\":" + std::to_string(s.color.wideColorUserEnabled) +
        ",\"cycle_precondition\":" + (HdrReady(s) ? "true" : "false"));
}
int Inspect() {
    Snapshot s{};
    const LONG rc = ReadDisplay(s);
    Log("inspect_api", ",\"return_code\":" + std::to_string(rc) + ",\"get_type\":15,\"get_size\":36,\"set_type\":16,\"set_size\":24,\"setters_executed\":0");
    if (rc == ERROR_SUCCESS) LogSnapshot("inspect", s);
    return rc == ERROR_SUCCESS ? 0 : 2;
}
constexpr DWORD kGuardianMs = 8000;
constexpr UINT32 kMagic = 0x48524331;
struct Frozen {
    UINT32 magic = kMagic, size = sizeof(Frozen), controllerPid = 0, simulation = 0;
    ULONGLONG controllerCreation = 0;
    Snapshot initial{};
};
ULONGLONG CreationTime(HANDLE process) {
    FILETIME creation{}, exit{}, kernel{}, user{};
    if (!GetProcessTimes(process, &creation, &exit, &kernel, &user)) return 0;
    return (static_cast<ULONGLONG>(creation.dwHighDateTime) << 32) | creation.dwLowDateTime;
}
std::wstring ExePath() {
    wchar_t path[32768]{};
    const DWORD count = GetModuleFileNameW(nullptr, path, static_cast<DWORD>(std::size(path)));
    return count && count < std::size(path) ? std::wstring(path, count) : L"";
}
bool OutsideJobs(HANDLE process) {
    BOOL inJob = TRUE;
    return IsProcessInJob(process, nullptr, &inJob) && !inJob;
}
bool Duplicate(HANDLE from, Handle& to, DWORD access) {
    return DuplicateHandle(GetCurrentProcess(), from, GetCurrentProcess(), &to.h, access, TRUE, 0) != FALSE;
}
bool ParseHandle(const wchar_t* text, HANDLE& handle) {
    if (!text || !*text || *text < L'0' || *text > L'9') return false;
    wchar_t* end = nullptr;
    errno = 0;
    const auto number = _wcstoui64(text, &end, 10);
    if (errno || *end || !number || number == UINT64_MAX) return false;
    handle = reinterpret_cast<HANDLE>(static_cast<uintptr_t>(number));
    DWORD flags = 0;
    return GetHandleInformation(handle, &flags) && (flags & HANDLE_FLAG_INHERIT) != 0;
}
Snapshot FakeSnapshot() {
    Snapshot s{};
    wcscpy_s(s.gdi, kDisplay);
    wcscpy_s(s.monitor, L"synthetic:own-memory-only");
    s.sourceAdapter.LowPart = 42; s.targetAdapter.LowPart = 42;
    s.sourceId = 1; s.targetId = 7; s.connector = 9;
    s.color.advancedColorSupported = 1;
    s.color.advancedColorActive = 1;
    s.color.highDynamicRangeSupported = 1;
    s.color.highDynamicRangeUserEnabled = 1;
    s.color.wideColorSupported = 1;
    s.color.wideColorUserEnabled = 1;
    s.color.activeColorMode = DISPLAYCONFIG_ADVANCED_COLOR_MODE_HDR;
    return s;
}
bool gFakeHdr = true;
LONG ReadCurrent(const Frozen& frozen, Snapshot& current) {
    if (!frozen.simulation) return ReadDisplay(current);
    current = frozen.initial;
    current.color.advancedColorActive = gFakeHdr ? 1u : 0u;
    current.color.highDynamicRangeUserEnabled = gFakeHdr ? 1u : 0u;
    current.color.activeColorMode = gFakeHdr ? DISPLAYCONFIG_ADVANCED_COLOR_MODE_HDR : DISPLAYCONFIG_ADVANCED_COLOR_MODE_SDR;
    return ERROR_SUCCESS;
}
// A different monitor identity fails immediately; only absence/API races retry.
LONG Revalidate(const Frozen& frozen, Snapshot& current, DWORD timeoutMs = 1500) {
    const ULONGLONG end = GetTickCount64() + timeoutMs;
    LONG rc = ERROR_NOT_FOUND;
    do {
        rc = ReadCurrent(frozen, current);
        if (rc == ERROR_SUCCESS) return SameTarget(frozen.initial, current) ? ERROR_SUCCESS : ERROR_INVALID_TARGET_HANDLE;
        if (rc == ERROR_DUP_NAME) return rc;
        Sleep(50);
    } while (GetTickCount64() < end);
    return rc;
}
LONG SetHdr(const Frozen& frozen, bool enable) {
    LONG rc = ERROR_SUCCESS;
    if (frozen.simulation) gFakeHdr = enable;
    else {
        DISPLAYCONFIG_SET_HDR_STATE request{};
        request.header = { DISPLAYCONFIG_DEVICE_INFO_SET_HDR_STATE, sizeof(request),
            frozen.initial.targetAdapter, frozen.initial.targetId };
        request.enableHdr = enable ? 1u : 0u;
        rc = DisplayConfigSetDeviceInfo(&request.header);
    }
    Log("hdr_setter", ",\"enable_hdr\":" + std::string(enable ? "true" : "false") +
        ",\"simulation\":" + (frozen.simulation ? "true" : "false") + ",\"return_code\":" + std::to_string(rc));
    return rc;
}
bool RestoreHdr(const Frozen& frozen) {
    Snapshot current{};
    LONG rc = Revalidate(frozen, current, 2000);
    if (rc != ERROR_SUCCESS) {
        Log("restore_refused_target", ",\"return_code\":" + std::to_string(rc));
        return false;
    }
    // Force ON even when a failed/pending OFF has not yet appeared in a query.
    // Only HDR state changes; never use SET_WCG_STATE or legacy Advanced Color.
    rc = SetHdr(frozen, true);
    const ULONGLONG end = GetTickCount64() + 2000;
    unsigned stable = 0;
    do {
        const LONG read = ReadCurrent(frozen, current);
        if (read == ERROR_SUCCESS && !SameTarget(frozen.initial, current)) break;
        if (read == ERROR_SUCCESS && Restored(frozen.initial, current)) {
            if (++stable >= 3) {
                LogSnapshot("restore_verified", current);
                return true; // Three observations; setter errors may be ambiguous.
            }
        } else stable = 0;
        Sleep(100);
    } while (GetTickCount64() < end);
    Log("restore_not_verified", ",\"setter_return_code\":" + std::to_string(rc));
    return false;
}

// Internal child command accepts inherited handles, never an arbitrary process ID.
int Guardian(int argc, wchar_t** argv) {
    if (argc != 7) return 2;
    HANDLE raw[5]{};
    for (int i = 0; i != 5; ++i) if (!ParseHandle(argv[i + 2], raw[i])) return 2;
    Handle mapping(raw[0]), controller(raw[1]), ready(raw[2]), done(raw[3]), gate(raw[4]);
    const auto* memory = static_cast<const Frozen*>(MapViewOfFile(mapping.h, FILE_MAP_READ, 0, 0, sizeof(Frozen)));
    if (!memory) return 2;
    const Frozen frozen = *memory;
    UnmapViewOfFile(memory);
    wchar_t parentExe[32768]{};
    DWORD parentChars = static_cast<DWORD>(std::size(parentExe));
    const std::wstring self = ExePath();
    if (frozen.magic != kMagic || frozen.size != sizeof(Frozen) || frozen.simulation > 1 ||
        frozen.controllerPid == GetCurrentProcessId() || frozen.controllerPid != GetProcessId(controller.h) ||
        !frozen.controllerCreation || frozen.controllerCreation != CreationTime(controller.h) ||
        self.empty() || !QueryFullProcessImageNameW(controller.h, 0, parentExe, &parentChars) ||
        _wcsicmp(self.c_str(), parentExe) != 0 || !OutsideJobs(GetCurrentProcess()) ||
        !HdrReady(frozen.initial) || _wcsicmp(frozen.initial.gdi, kDisplay) != 0) return 2;
    Snapshot current{};
    if (Revalidate(frozen, current, 1000) != ERROR_SUCCESS || !Restored(frozen.initial, current)) return 2;
    if (!SetEvent(ready.h)) return 2;

    // NO allocation/logging/display calls after READY until DONE or controller exit.
    // On timeout terminate ONLY the exact inherited same-exe controller handle.
    // Wait for its confirmed exit BEFORE ON: an alive controller could do late OFF.
    const HANDLE waits[] = { done.h, controller.h };
    DWORD state = WaitForMultipleObjects(2, waits, FALSE, kGuardianMs);
    bool forced = false;
    while (state != WAIT_OBJECT_0 && state != WAIT_OBJECT_0 + 1) {
        forced = true;
        TerminateProcess(controller.h, 124);
        state = WaitForMultipleObjects(2, waits, FALSE, 250);
        // Never abandon the guard if OS termination is delayed/denied. DONE is
        // trusted only because controller signals it after no-future-OFF commits.
    }
    if (state == WAIT_OBJECT_0) {
        Log("guardian_done", ",\"restore_needed\":false");
        return 0;
    }
    gFakeHdr = false;
    const bool restored = RestoreHdr(frozen);
    Log("guardian_parent_exited", ",\"forced\":" + std::string(forced ? "true" : "false") +
        ",\"parent_exit_confirmed\":true,\"simulation\":" + (frozen.simulation ? "true" : "false"));
    Log("guardian_finished", ",\"restored\":" + std::string(restored ? "true" : "false"));
    return restored ? 0 : 2;
}
struct Supervisor {
    Handle process, ready, done, gate;
    DWORD pid = 0;
    bool Start(const Frozen& frozen) {
        SetLastError(ERROR_SUCCESS);
        gate.h = CreateMutexW(nullptr, FALSE, frozen.simulation ?
            L"Local\\HDRCalibrate.HdrCycle.Simulation" : L"Local\\HDRCalibrate.HdrCycle.DISPLAY1");
        if (!gate.h || GetLastError() == ERROR_ALREADY_EXISTS) return false;
        ready.h = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        done.h = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        Handle writable(CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, sizeof(Frozen), nullptr));
        if (!ready.h || !done.h || !writable.h) return false;
        void* memory = MapViewOfFile(writable.h, FILE_MAP_WRITE, 0, 0, sizeof(Frozen));
        if (!memory) return false;
        memcpy(memory, &frozen, sizeof(frozen));
        UnmapViewOfFile(memory);
        Handle readOnly, ownController, childReady, childDone, childGate;
        if (!Duplicate(writable.h, readOnly, FILE_MAP_READ) ||
            !Duplicate(GetCurrentProcess(), ownController, SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_TERMINATE) ||
            !Duplicate(ready.h, childReady, EVENT_MODIFY_STATE | SYNCHRONIZE) ||
            !Duplicate(done.h, childDone, SYNCHRONIZE) || !Duplicate(gate.h, childGate, SYNCHRONIZE)) return false;
        CloseHandle(writable.h); writable.h = nullptr; // No writable mapping remains.
        Handle nullFile(CreateFileW(L"NUL", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, 0, nullptr));
        Handle childNull, childOutput;
        if (!nullFile.h || nullFile.h == INVALID_HANDLE_VALUE || !Duplicate(nullFile.h, childNull, GENERIC_READ | GENERIC_WRITE)) return false;
        const HANDLE output = GetStdHandle(STD_OUTPUT_HANDLE);
        if (GetFileType(output) == FILE_TYPE_DISK && !Duplicate(output, childOutput, GENERIC_WRITE)) return false;
        const HANDLE childOut = childOutput.h ? childOutput.h : childNull.h;
        HANDLE inherited[] = { readOnly.h, ownController.h, childReady.h, childDone.h, childGate.h, childNull.h, childOut };
        SIZE_T attributeBytes = 0;
        InitializeProcThreadAttributeList(nullptr, 1, 0, &attributeBytes);
        std::vector<unsigned char> attributeMemory(attributeBytes);
        auto* attributes = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(attributeMemory.data());
        if (!InitializeProcThreadAttributeList(attributes, 1, 0, &attributeBytes)) return false;
        const SIZE_T handleBytes = childOutput.h ? sizeof(inherited) : sizeof(inherited) - sizeof(HANDLE);
        const BOOL attributed = UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST,
            inherited, handleBytes, nullptr, nullptr);
        STARTUPINFOEXW startup{};
        startup.StartupInfo.cb = sizeof(startup);
        startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
        startup.StartupInfo.hStdInput = childNull.h;
        startup.StartupInfo.hStdOutput = startup.StartupInfo.hStdError = childOut;
        startup.lpAttributeList = attributes;
        const std::wstring exe = ExePath();
        std::wstring command = L"\"" + exe + L"\" --restore";
        for (unsigned i = 0; i != 5; ++i) command += L" " + std::to_wstring(reinterpret_cast<uintptr_t>(inherited[i]));
        PROCESS_INFORMATION pi{};
        const BOOL created = attributed && !exe.empty() && CreateProcessW(exe.c_str(), command.data(), nullptr, nullptr, TRUE,
            CREATE_NO_WINDOW | CREATE_SUSPENDED | CREATE_BREAKAWAY_FROM_JOB | EXTENDED_STARTUPINFO_PRESENT,
            nullptr, nullptr, &startup.StartupInfo, &pi);
        const DWORD createError = GetLastError();
        DeleteProcThreadAttributeList(attributes);
        if (!created) { Log("guardian_start_refused", ",\"error\":" + std::to_string(createError)); return false; }
        process.h = pi.hProcess; Handle thread(pi.hThread); pid = pi.dwProcessId;
        // Breakaway must leave ALL jobs, not merely the immediate parent job.
        BOOL childInJob = TRUE;
        const BOOL queriedJob = IsProcessInJob(process.h, nullptr, &childInJob);
        const DWORD jobError = queriedJob ? ERROR_SUCCESS : GetLastError();
        const DWORD resumed = queriedJob && !childInJob ? ResumeThread(thread.h) : static_cast<DWORD>(-1);
        const DWORD resumeError = queriedJob && !childInJob && resumed == static_cast<DWORD>(-1) ? GetLastError() : ERROR_SUCCESS;
        if (!queriedJob || childInJob || resumed == static_cast<DWORD>(-1)) {
            TerminateProcess(process.h, 125); // Only our own newly created helper.
            WaitForSingleObject(process.h, 3000);
            Log("guardian_job_isolation_refused", ",\"is_process_in_job_query\":" + std::to_string(queriedJob) +
                ",\"in_job\":" + std::to_string(childInJob) + ",\"job_error\":" + std::to_string(jobError) +
                ",\"resume_error\":" + std::to_string(resumeError));
            return false;
        }
        const HANDLE wait[] = { ready.h, process.h };
        const DWORD state = WaitForMultipleObjects(2, wait, FALSE, 3000);
        if (state != WAIT_OBJECT_0 || WaitForSingleObject(process.h, 0) != WAIT_TIMEOUT) {
            // No OFF can happen on this return path. Commit permanent cancellation.
            SetEvent(done.h);
            WaitForSingleObject(process.h, 3000);
            Log("guardian_handshake_refused");
            return false;
        }
        Log("guardian_ready", ",\"guardian_pid\":" + std::to_string(pid) +
            ",\"outside_all_jobs\":true,\"timeout_ms\":8000,\"simulation\":" + (frozen.simulation ? "true" : "false"));
        return true;
    }
    bool Complete() {
        if (!SetEvent(done.h) || WaitForSingleObject(process.h, 3000) != WAIT_OBJECT_0) return false;
        DWORD code = 2;
        return GetExitCodeProcess(process.h, &code) && code == 0;
    }
};
int Cycle(const std::wstring& simulation) {
    Frozen frozen{};
    frozen.controllerPid = GetCurrentProcessId();
    frozen.controllerCreation = CreationTime(GetCurrentProcess());
    frozen.simulation = simulation.empty() ? 0u : 1u;
    const LONG rc = frozen.simulation ? (frozen.initial = FakeSnapshot(), ERROR_SUCCESS) : ReadDisplay(frozen.initial);
    if (rc != ERROR_SUCCESS || !HdrReady(frozen.initial) || !frozen.controllerCreation) {
        Log("cycle_precondition_refused", ",\"return_code\":" + std::to_string(rc)); return 2;
    }
    LogSnapshot("before", frozen.initial);
    Supervisor supervisor;
    if (!supervisor.Start(frozen)) return 2;
    if (simulation == L"late-off") Sleep(kGuardianMs + 500); // Child must kill us before OFF.
    Snapshot current{};
    const LONG recheck = Revalidate(frozen, current);
    if (recheck != ERROR_SUCCESS || !Restored(frozen.initial, current) ||
        WaitForSingleObject(supervisor.process.h, 0) != WAIT_TIMEOUT) {
        // This branch returns permanently: no OFF has been called or can follow.
        supervisor.Complete();
        Log("cancelled_before_off", ",\"return_code\":" + std::to_string(recheck)); return 2;
    }
    // Exactly ONE OFF call. Any error/exception after this point leaves DONE
    // unsignalled unless ON has been independently verified. Child restores on exit.
    const LONG off = SetHdr(frozen, false);
    if (simulation == L"exit-after-off") ExitProcess(25);
    if (off == ERROR_SUCCESS) Sleep(frozen.simulation ? 30 : 1500);
    const LONG offRead = ReadCurrent(frozen, current);
    const bool offObserved = offRead == ERROR_SUCCESS && SameTarget(frozen.initial, current) &&
        (current.color.value & 0xFFFFFF04u) == 0 && !current.color.highDynamicRangeUserEnabled &&
        (current.color.activeColorMode == DISPLAYCONFIG_ADVANCED_COLOR_MODE_SDR ||
         current.color.activeColorMode == DISPLAYCONFIG_ADVANCED_COLOR_MODE_WCG);
    Log("off_observation", ",\"observed\":" + std::string(offObserved ? "true" : "false") +
        ",\"return_code\":" + std::to_string(offRead));
    if (!RestoreHdr(frozen)) return 2; // Parent exit triggers forced ON recovery.
    if (!supervisor.Complete()) { Log("guardian_completion_unverified"); return 2; }
    Log("cycle_finished", ",\"off_calls\":1,\"restored\":true,\"off_observed\":" +
        std::string(offObserved ? "true" : "false") + ",\"simulation\":" + (frozen.simulation ? "true" : "false"));
    return off == ERROR_SUCCESS && offObserved ? 0 : 2;
}
int SelfTest() {
    unsigned checks = 0, failures = 0;
    auto check = [&](bool ok) { ++checks; if (!ok) ++failures; };
    Snapshot initial = FakeSnapshot(), changed = initial;
    check(HdrReady(initial)); check(Restored(initial, initial));
    changed.targetId++; check(!SameTarget(initial, changed)); changed = initial;
    changed.sourceId++; check(!SameTarget(initial, changed)); changed = initial;
    changed.targetAdapter.HighPart++; check(!SameTarget(initial, changed)); changed = initial;
    changed.sourceAdapter.LowPart++; check(!SameTarget(initial, changed)); changed = initial;
    changed.monitor[0] = L'x'; check(!SameTarget(initial, changed)); changed = initial;
    changed.connector++; check(!SameTarget(initial, changed)); changed = initial;
    changed.gdi[0] = L'x'; check(!SameTarget(initial, changed)); changed = initial;
    changed.color.wideColorUserEnabled = 0; check(!Restored(initial, changed)); changed = initial;
    changed.color.advancedColorLimitedByPolicy = 1; check(!HdrReady(changed)); changed = initial;
    changed.color.highDynamicRangeSupported = 0; check(!HdrReady(changed)); changed = initial;
    changed.color.highDynamicRangeUserEnabled = 0; check(!HdrReady(changed)); changed = initial;
    changed.color.advancedColorActive = 0; check(!HdrReady(changed)); changed = initial;
    changed.color.activeColorMode = DISPLAYCONFIG_ADVANCED_COLOR_MODE_WCG; check(!HdrReady(changed));
    changed = initial; changed.color.value |= 4u; check(!HdrReady(changed));
    changed = initial; changed.color.value |= 0x100u; check(!HdrReady(changed));
    HANDLE invalid = nullptr;
    check(!ParseHandle(L"0", invalid)); check(!ParseHandle(L"-1", invalid));
    check(!ParseHandle(L"1x", invalid)); check(!ParseHandle(L"18446744073709551616", invalid));
    DISPLAYCONFIG_SET_HDR_STATE request{};
    request.enableHdr = 1; check(request.value == 1);
    request.enableHdr = 0; check(request.value == 0);
    Log("self_test", ",\"checks\":" + std::to_string(checks) + ",\"failures\":" + std::to_string(failures) +
        ",\"display_api_calls\":0,\"get_size\":36,\"set_size\":24");
    return failures ? 2 : 0;
}
} // namespace
int wmain(int argc, wchar_t** argv) {
    try {
        if (argc == 1 || (argc == 2 && wcscmp(argv[1], L"--inspect") == 0)) return Inspect();
        if (argc == 2 && wcscmp(argv[1], L"--self-test") == 0) return SelfTest();
        if (argc == 3 && wcscmp(argv[1], L"--cycle") == 0 && _wcsicmp(argv[2], kDisplay) == 0) return Cycle(L"");
        if (argc == 3 && wcscmp(argv[1], L"--simulate") == 0 &&
            (wcscmp(argv[2], L"normal") == 0 || wcscmp(argv[2], L"exit-after-off") == 0 || wcscmp(argv[2], L"late-off") == 0)) return Cycle(argv[2]);
        if (argc >= 2 && wcscmp(argv[1], L"--restore") == 0) return Guardian(argc, argv);
        Log("usage", ",\"syntax\":\"--inspect | --cycle DISPLAY1_GDI_PATH | --self-test | --simulate normal/exit-after-off/late-off\",\"setters_executed\":0");
    } catch (...) {
        // An armed controller never signals DONE here. The child owns recovery.
        Log("exception", ",\"guardian_recovery_on_controller_exit\":true");
    }
    return 2;
}
