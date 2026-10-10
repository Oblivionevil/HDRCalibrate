// Ordinary console host only. No DWM discovery, preflight, remote memory,
// external application attachment or desktop/window operation is performed.
#define wmain ControllerEntryForTests
#include "Controller.cpp"
#undef wmain

namespace {
int passed = 0;
int failed = 0;
void Check(const char* name, bool value) {
    if (value) ++passed; else ++failed;
    std::printf("%s %s\n", value ? "PASS" : "FAIL", name);
}
bool ParseArgs(std::vector<std::wstring> arguments, Options& result) {
    arguments.insert(arguments.begin(), L"ControllerSelfTest.exe");
    std::vector<wchar_t*> pointers;
    for (auto& argument : arguments) pointers.push_back(argument.data());
    return Parse(static_cast<int>(pointers.size()), pointers.data(), result);
}
void ParserTests() {
    for (const auto* path : {L"C:\\Programs\\AnyApp.exe", L"d:\\a.exe", L"C:\\Program Files\\\u00dcber\\App.exe"})
        Check("ordinary-absolute-local-path-accepted", AbsoluteLocalPath(path));
    for (const auto* path : {L"", L"app.exe", L"C:app.exe", L"\\app.exe", L"\\\\server\\share\\app.exe",
        L"\\\\?\\C:\\app.exe", L"\\\\.\\C:\\app.exe", L"C:\\app.exe:stream", L"C:\\a\\..\\app.exe",
        L"C:\\a\\.\\app.exe", L"C:\\a\\\\app.exe", L"C:\\a \\app.exe", L"C:\\app.exe.",
        L"C:\\app.exe ", L"C:\\a*\\app.exe", L"C:\\a?\\app.exe", L"C:\\", L"C:/app.exe", L"C:\\app\n.exe"})
        Check("ambiguous-relative-device-or-network-path-rejected", !AbsoluteLocalPath(path));
    Check("JSON-exact-backslash-and-Unicode-escaping", JsonString(L"C:\\P\u00fc\\App.exe") == "\"C:\\\\P\\u00fc\\\\App.exe\"");
    const std::vector<std::wstring> legacy{L"--observe", L"--dll", L"C:\\Hook.dll", L"--target-pid", L"1", L"--target-hwnd", L"2"};
    Options options;
    Check("legacy-experimental-request-compatible", ParseArgs(legacy, options) && options.targetExe.empty() && !options.continuous);
    const auto test = [&](const std::vector<std::wstring>& extra, bool expected, const char* name) {
        auto arguments = legacy; arguments.insert(arguments.end(), extra.begin(), extra.end());
        Options parsed;
        Check(name, ParseArgs(arguments, parsed) == expected);
    };
    test({L"--target-exe", L"C:\\Any App.exe", L"--target-creation-filetime", L"134331673378020485"}, true, "exact-target-pair-parses");
    test({L"--target-exe", L"C:\\Any.exe"}, false, "exe-without-generation-rejected");
    test({L"--target-creation-filetime", L"3"}, false, "generation-without-exe-rejected");
    test({L"--target-exe", L"C:\\Any.exe", L"--target-creation-filetime", L"0"}, false, "zero-target-generation-rejected");
    test({L"--target-exe", L"C:\\Any.exe", L"--target-creation-filetime", L"9223372036854775808"}, false, "target-generation-overflow-rejected");
    test({L"--continuous"}, false, "continuous-without-managed-owner-rejected");
    test({L"--owner-pid", L"4", L"--owner-creation-filetime", L"5"}, false, "owner-without-pipe-rejected");
    const std::vector<std::wstring> managed{L"--target-exe", L"C:\\Any.exe", L"--target-creation-filetime", L"3",
        L"--expected-dwm-pid", L"4", L"--expected-dwm-creation-filetime", L"5", L"--owner-pid", L"6",
        L"--owner-creation-filetime", L"7", L"--pipe", L"HDRCalibrate.ProgramGamma.0123456789abcdef0123456789ABCDEF"};
    test(managed, true, "managed-exact-owner-target-and-DWM-pairs-accepted");
    auto continuous = managed; continuous.push_back(L"--continuous");
    test(continuous, true, "continuous-requires-complete-managed-contract");
    continuous.push_back(L"--continuous");
    test(continuous, false, "duplicate-continuous-rejected");
    for (const auto* name : {L"pipe", L"HDRCalibrate.ProgramGamma.0123", L"\\\\.\\pipe\\HDRCalibrate.ProgramGamma.0123456789abcdef0123456789abcdef",
        L"HDRCalibrate.ProgramGamma.0123456789abcdef0123456789abcdeg", L"HDRCalibrate.ProgramGamma.0123456789abcdef0123456789abcdef\\child"})
        Check("noncanonical-managed-pipe-name-rejected", !ValidPipeName(name));
}
void MetadataTests() {
    Handle selected(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, GetCurrentProcessId()));
    Options options;
    options.targetPid = GetCurrentProcessId();
    options.targetCreation = CreationTime(GetCurrentProcess());
    wchar_t image[32768]{}; DWORD length = static_cast<DWORD>(std::size(image));
    const bool queried = QueryFullProcessImageNameW(GetCurrentProcess(), 0, image, &length) != FALSE;
    options.targetExe.assign(image, length);
    std::uint64_t created = 0;
    std::wstring executable;
    Check("arbitrary-console-host-accepted-through-metadata-only-handle", queried && selected.value &&
        VerifyTargetMetadata(selected.value, options, created, executable) == ERROR_SUCCESS &&
        created == options.targetCreation && executable == options.targetExe);
    auto changed = options; ++changed.targetPid;
    Check("selected-handle-rejects-wrong-PID", VerifyTargetMetadata(selected.value, changed, created, executable) == ERROR_INVALID_OWNER);
    changed = options; ++changed.targetCreation;
    Check("selected-handle-rejects-wrong-generation", VerifyTargetMetadata(selected.value, changed, created, executable) == ERROR_INVALID_DATA);
    changed = options; changed.targetExe += L".different";
    Check("selected-handle-rejects-different-executable", VerifyTargetMetadata(selected.value, changed, created, executable) == ERROR_INVALID_NAME);
    changed = options; CharUpperBuffW(changed.targetExe.data(), static_cast<DWORD>(changed.targetExe.size()));
    Check("selected-executable-comparison-is-ordinal-case-insensitive", VerifyTargetMetadata(selected.value, changed, created, executable) == ERROR_SUCCESS);
    Check("invalid-metadata-handle-rejected", VerifyTargetMetadata(nullptr, options, created, executable) != ERROR_SUCCESS);
    Request request{};
    request.targetPid = options.targetPid;
    request.targetHwnd = 1;
    Check("invalid-window-cannot-retain-target-identity", !StillTarget(selected.value, options.targetCreation, request));
    std::wstring command = L"\"" + options.targetExe + L"\" --exit-immediately";
    STARTUPINFOW startup{}; startup.cb = sizeof(startup);
    PROCESS_INFORMATION child{};
    const bool launched = CreateProcessW(options.targetExe.c_str(), command.data(), nullptr, nullptr, FALSE,
        CREATE_NO_WINDOW, nullptr, nullptr, &startup, &child) != FALSE;
    Handle childProcess(child.hProcess), childThread(child.hThread);
    bool exitedRejected = false;
    if (launched && WaitForSingleObject(childProcess.value, 5000) == WAIT_OBJECT_0) {
        changed = options; changed.targetPid = child.dwProcessId; changed.targetCreation = CreationTime(childProcess.value);
        exitedRejected = VerifyTargetMetadata(childProcess.value, changed, created, executable) == ERROR_PROCESS_ABORTED;
    }
    Check("exited-self-test-process-cannot-be-selected", exitedRejected);
}
void CommandTests() {
    ManagedCommands state;
    Check("no-GO-means-no-activation", !ManagedHeartbeatFresh(state, 1000, 1000));
    AcceptManagedCommand(state, 1, 1000);
    Check("heartbeat-alone-cannot-commit", !ManagedHeartbeatFresh(state, 1000, 1000));
    AcceptManagedCommand(state, 3, 1000);
    Check("GO-commits-bounded-current-heartbeat", ManagedHeartbeatFresh(state, 2499, 1000));
    Check("heartbeat-expires-after-1500ms", !ManagedHeartbeatFresh(state, 2501, 1000));
    Check("future-heartbeat-rejected", !ManagedHeartbeatFresh(state, 999, 1000));
    Check("heartbeat-frequency-overflow-rejected", !ManagedHeartbeatFresh(state, 1000, INT64_MAX));
    AcceptManagedCommand(state, 2, 1100);
    AcceptManagedCommand(state, 1, 1101); AcceptManagedCommand(state, 3, 1102);
    Check("STOP-is-terminal-despite-later-GO-or-heartbeat", state.stopped && !ManagedHeartbeatFresh(state, 1102, 1000));
    state = {}; AcceptManagedCommand(state, 3, 1000); AcceptManagedCommand(state, 3, 1001);
    Check("duplicate-GO-fails-closed", state.failed);
    state = {}; AcceptManagedCommand(state, 0, 1000); AcceptManagedCommand(state, 3, 1001);
    Check("unknown-command-fails-closed", state.failed && !state.committed);
    Request lease{};
    lease.magic = kProtocolMagic; lease.version = kProtocolVersion; lease.size = sizeof(lease);
    lease.nonceLow = 42; lease.targetPid = 100; lease.targetCreationTime = 500; lease.targetHwnd = 200;
    lease.expectedVisual = 300; lease.expectedSurface = 400; lease.expectedBind = 600;
    lease.mode = Mode::CandidateVeto; lease.qpcFrequency = 1000; lease.leaseIssuedQpc = 1000; lease.leaseDeadlineQpc = 6000;
    Check("renewal-retains-same-session-and-exact-surface-identity", RenewManagedLease(lease, 3500, 5000) &&
        lease.leaseIssuedQpc == 3500 && lease.leaseDeadlineQpc == 8500 && lease.nonceLow == 42 &&
        lease.targetPid == 100 && lease.targetCreationTime == 500 && lease.targetHwnd == 200 &&
        lease.expectedVisual == 300 && lease.expectedSurface == 400 && lease.expectedBind == 600 && lease.mode == Mode::CandidateVeto);
    Check("renewal-cannot-raise-five-second-ceiling", !RenewManagedLease(lease, 4000, 5001));
    Check("renewal-cannot-resurrect-expired-lease", !RenewManagedLease(lease, 8500, 5000));
    Check("renewal-cannot-publish-future-issued-lease", !RenewManagedLease(lease, 3499, 5000));
    Sample sample{};
    sample.qpc = 4100; sample.point = HookPoint::Candidate; sample.stage = Stage::Complete;
    sample.hwnd = lease.targetHwnd; sample.ownerPid = lease.targetPid;
    sample.flags = OwnershipVerified | HasPixelFormat | HasBindInfo;
    sample.colorSpace = 0; sample.pixelFormat = 87;
    sample.visual = lease.expectedVisual; sample.surface = lease.expectedSurface; sample.bind = lease.expectedBind;
    sample.decision = Decision::Veto;
    Check("current-exact-SDR-surface-does-not-stop-managed-session", !ManagedSurfaceRejected(lease, sample, 4200, 3500));
    sample.decision = Decision::NotVerifiedSdr; sample.colorSpace = 12;
    Check("fresh-selected-HDR-transition-stops-managed-session", ManagedSurfaceRejected(lease, sample, 4200, 3500));
    Check("stale-HDR-sample-does-not-stop-managed-session", !ManagedSurfaceRejected(lease, sample, 5101, 3500));
    Check("pre-arm-sample-cannot-stop-current-managed-session", !ManagedSurfaceRejected(lease, sample, 4200, 4150));
    ++sample.ownerPid;
    Check("other-process-HDR-sample-cannot-stop-selected-session", !ManagedSurfaceRejected(lease, sample, 4200, 3500));
    sample.ownerPid = lease.targetPid; sample.decision = Decision::SurfaceChanged;
    Check("fresh-selected-surface-rejection-stops-managed-session", ManagedSurfaceRejected(lease, sample, 4200, 3500));
    sample.decision = Decision::MissingObservation; sample.colorSpace = 0; ++sample.bind;
    Check("fresh-complete-replacement-tuple-stops-without-relearning", ManagedSurfaceRejected(lease, sample, 4200, 3500));
    sample.flags &= ~HasBindInfo;
    Check("incomplete-observation-does-not-claim-proven-replacement", !ManagedSurfaceRejected(lease, sample, 4200, 3500));
    lease.leaseIssuedQpc = INT64_MAX - 6000; lease.leaseDeadlineQpc = INT64_MAX - 1000;
    Check("renewal-deadline-overflow-rejected", !RenewManagedLease(lease, INT64_MAX - 2000, 5000));
}
bool WriteControl(HANDLE server, BYTE command) {
    Handle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
    OVERLAPPED operation{}; operation.hEvent = event.value;
    DWORD written = 0;
    if (WriteFile(server, &command, 1, &written, &operation)) return written == 1;
    if (GetLastError() != ERROR_IO_PENDING) return false;
    if (WaitForSingleObject(event.value, 1000) != WAIT_OBJECT_0) {
        CancelIoEx(server, &operation);
        GetOverlappedResult(server, &operation, &written, TRUE);
        return false;
    }
    return GetOverlappedResult(server, &operation, &written, FALSE) && written == 1;
}
void PipeTests() {
    wchar_t suffix[33]{};
    swprintf_s(suffix, L"%016llx%016llx", static_cast<unsigned long long>(GetCurrentProcessId()), static_cast<unsigned long long>(Qpc()));
    const std::wstring name = std::wstring(L"HDRCalibrate.ProgramGamma.") + suffix;
    const auto path = L"\\\\.\\pipe\\" + name;
    Handle server(CreateNamedPipeW(path.c_str(), PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED,
        PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS, 1, 65536, 65536, 0, nullptr));
    Handle connected(CreateEventW(nullptr, TRUE, FALSE, nullptr));
    OVERLAPPED connection{}; connection.hEvent = connected.value;
    const bool waiting = server.value != INVALID_HANDLE_VALUE &&
        (ConnectNamedPipe(server.value, &connection) || GetLastError() == ERROR_IO_PENDING);
    Options options; options.ownerPid = GetCurrentProcessId(); options.ownerCreation = CreationTime(GetCurrentProcess()); options.pipe = name;
    auto wrongOwner = options; ++wrongOwner.ownerCreation;
    auto* rejected = new ManagedPipe;
    Check("pipe-owner-generation-mismatch-is-rejected-before-connect", !rejected->Connect(wrongOwner));
    // The object intentionally survives to host process exit, exactly like the
    // production controller, so a pending canceled read never loses storage.
    auto* client = new ManagedPipe;
    const bool clientConnected = waiting && client->Connect(options);
    DWORD unused = 0;
    const bool serverConnected = waiting && WaitForSingleObject(connected.value, 1000) == WAIT_OBJECT_0 &&
        GetOverlappedResult(server.value, &connection, &unused, FALSE);
    Check("real-local-pipe-authenticates-held-owner-and-server-PID", clientConnected && serverConnected);
    if (!clientConnected || !serverConnected) return;
    Check("real-pipe-awaits-explicit-GO", !client->Healthy());
    Check("real-pipe-heartbeat-without-GO-does-not-authorize", WriteControl(server.value, 1) && !client->Healthy());
    Check("real-pipe-GO-authorizes", WriteControl(server.value, 3) && client->Healthy());
    client->Queue("{\"event\":\"self_test\"}\n");
    Handle readEvent(CreateEventW(nullptr, TRUE, FALSE, nullptr));
    OVERLAPPED read{}; read.hEvent = readEvent.value;
    char buffer[256]{}; DWORD received = 0;
    bool readOkay = ReadFile(server.value, buffer, sizeof(buffer) - 1, &received, &read) != FALSE;
    if (!readOkay && GetLastError() == ERROR_IO_PENDING) {
        readOkay = WaitForSingleObject(readEvent.value, 1000) == WAIT_OBJECT_0 && GetOverlappedResult(server.value, &read, &received, FALSE);
        if (!readOkay) { CancelIoEx(server.value, &read); GetOverlappedResult(server.value, &read, &received, TRUE); }
    }
    Check("real-pipe-carries-complete-JSON-line", readOkay && std::string(buffer, received) == "{\"event\":\"self_test\"}\n");
    Check("real-pipe-STOP-is-observed-without-blocking", WriteControl(server.value, 2) && !client->Healthy() && client->StopRequested());
    DisconnectNamedPipe(server.value);
    const auto reconnect = [&]() -> ManagedPipe* {
        ResetEvent(connected.value);
        connection = {}; connection.hEvent = connected.value;
        if (!ConnectNamedPipe(server.value, &connection) && GetLastError() != ERROR_IO_PENDING) return nullptr;
        auto* next = new ManagedPipe;
        if (!next->Connect(options) || WaitForSingleObject(connected.value, 1000) != WAIT_OBJECT_0 ||
            !GetOverlappedResult(server.value, &connection, &unused, FALSE) || !WriteControl(server.value, 3) || !next->Healthy()) return nullptr;
        return next;
    };
    auto* disconnected = reconnect();
    DisconnectNamedPipe(server.value);
    Check("real-pipe-disconnect-revokes-health", disconnected && !disconnected->Healthy() && disconnected->Error());
    auto* unread = reconnect();
    if (unread) {
        for (int count = 0; count != 12; ++count) unread->Queue(std::string(16000, 'x'));
        Sleep(550);
    }
    Check("unread-pipe-output-fails-closed-with-bounded-buffer", unread && !unread->Healthy() &&
        (unread->Error() == ERROR_TIMEOUT || unread->Error() == ERROR_BUFFER_OVERFLOW));
    DisconnectNamedPipe(server.value);
    auto* expired = reconnect();
    if (expired) Sleep(1550);
    Check("queued-heartbeat-cannot-revive-expired-native-watchdog", expired && WriteControl(server.value, 1) &&
        !expired->Healthy() && expired->Error() == ERROR_TIMEOUT);
    DisconnectNamedPipe(server.value);
}
void ModuleTests(const wchar_t* hookPath) {
    Handle file(CreateFileW(hookPath, GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr));
    const HMODULE module = file.value != INVALID_HANDLE_VALUE ? LoadLibraryW(hookPath) : nullptr;
    Check("ordinary-host-loads-separate-test-DLL-without-hooks", module != nullptr);
    if (!module) return;
    Module conflict;
    Check("exact-existing-hook-path-is-allowed-for-reuse", CheckExclusiveHookPath(GetCurrentProcessId(), hookPath, conflict) == ERROR_SUCCESS && !conflict.base);
    Check("different-existing-hook-path-prevents-second-copy", CheckExclusiveHookPath(GetCurrentProcessId(), L"C:\\different\\HDRCalibrate.SelectiveDwmHook.dll", conflict) == ERROR_ALREADY_EXISTS && conflict.base);
    // Only the ordinary self-test host releases its never-started load reference.
    FreeLibrary(module);
}
}
int wmain(int argc, wchar_t** argv) {
    if (argc == 2 && wcscmp(argv[1], L"--exit-immediately") == 0) return 0;
    std::puts("Controller host tests: local metadata, parser and local IPC only; no DWM/game/desktop operations.");
    ParserTests(); MetadataTests(); CommandTests(); PipeTests();
    if (argc == 2) ModuleTests(argv[1]);
    std::printf("RESULT %d passed; %d failed\n", passed, failed);
    return failed ? 1 : 0;
}
