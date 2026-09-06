// A separate, ordinary console process. This never loads dwmcore, attaches to a
// game, or changes the desktop. Only these two synthetic host functions are
// detoured. Synthetic observations test the production dispatch/policy; they do
// not substitute for live DWM ownership, image, HDR, VRR, or latency evidence.
#define SELECTIVE_DWM_SELF_TEST
#include "SelectiveDwmHook.cpp"
#include <cstdio>

namespace {

std::uint64_t g_received[13]{};
unsigned int g_nativeCandidateCalls = 0;
unsigned int g_nativeFlipCalls = 0;
constexpr LONG kNativeResult = static_cast<LONG>(0x81234567U);
constexpr DWORD kNativeError = 4317;
const std::uint64_t kArguments[13] = {
    0x1122334455667700ULL, 0x2233445566778810ULL, 0x3344556677889920ULL,
    0x445566778899AA30ULL, 0x5566778899AABB40ULL, 0x66778899AABBCC50ULL,
    0x778899AABBCCDD60ULL, 0x8899AABBCCDDEE70ULL, 0x99AABBCCDDEEFF80ULL,
    0xAABBCCDDEEFF0090ULL, 0xFEDCBA98U, 0x89ABCDEFU, 1
};

__declspec(noinline) LONG __fastcall NativeCandidate(void* self, void* visual, const void* rect0,
    void* surface, void* realization, const void* rect1, const void* rect2, const void* rect3,
    const void* corners, const void* matrix, std::uint32_t rotation, std::uint32_t flags, bool option) {
    g_received[0] = reinterpret_cast<std::uint64_t>(self);
    g_received[1] = reinterpret_cast<std::uint64_t>(visual);
    g_received[2] = reinterpret_cast<std::uint64_t>(rect0);
    g_received[3] = reinterpret_cast<std::uint64_t>(surface);
    g_received[4] = reinterpret_cast<std::uint64_t>(realization);
    g_received[5] = reinterpret_cast<std::uint64_t>(rect1);
    g_received[6] = reinterpret_cast<std::uint64_t>(rect2);
    g_received[7] = reinterpret_cast<std::uint64_t>(rect3);
    g_received[8] = reinterpret_cast<std::uint64_t>(corners);
    g_received[9] = reinterpret_cast<std::uint64_t>(matrix);
    g_received[10] = rotation;
    g_received[11] = flags;
    g_received[12] = option ? 1 : 0;
    ++g_nativeCandidateCalls;
    SetLastError(kNativeError);
    return kNativeResult;
}

__declspec(noinline) LONG __fastcall NativeFlip(void* self, void* plane, bool enable) {
    g_received[0] = reinterpret_cast<std::uint64_t>(self);
    g_received[1] = reinterpret_cast<std::uint64_t>(plane);
    g_received[2] = enable ? 1 : 0;
    ++g_nativeFlipCalls;
    SetLastError(kNativeError);
    return kNativeResult;
}

CandidateFunction volatile g_callCandidate = &NativeCandidate;
IndependentFlipFunction volatile g_callFlip = &NativeFlip;

LONG Invoke(bool option = true) {
    return g_callCandidate(reinterpret_cast<void*>(kArguments[0]), reinterpret_cast<void*>(kArguments[1]),
        reinterpret_cast<const void*>(kArguments[2]), reinterpret_cast<void*>(kArguments[3]),
        reinterpret_cast<void*>(kArguments[4]), reinterpret_cast<const void*>(kArguments[5]),
        reinterpret_cast<const void*>(kArguments[6]), reinterpret_cast<const void*>(kArguments[7]),
        reinterpret_cast<const void*>(kArguments[8]), reinterpret_cast<const void*>(kArguments[9]),
        static_cast<std::uint32_t>(kArguments[10]), static_cast<std::uint32_t>(kArguments[11]), option);
}

void Publish(SharedState* shared, Request request) {
    const auto before = Read64(&shared->request.sequence);
    request.sequence = (before & ~1LL) + 2;
    InterlockedExchange64(Atomic64(&shared->request.sequence), request.sequence - 1);
    std::memcpy(reinterpret_cast<std::uint8_t*>(&shared->request) + sizeof(request.sequence),
        reinterpret_cast<const std::uint8_t*>(&request) + sizeof(request.sequence),
        sizeof(request) - sizeof(request.sequence));
    MemoryBarrier();
    InterlockedExchange64(Atomic64(&shared->request.sequence), request.sequence);
}

Request Lease(const Session& session, Mode mode) {
    auto request = session.identity;
    request.leaseIssuedQpc = Qpc();
    request.leaseDeadlineQpc = request.leaseIssuedQpc + g_frequency * 5;
    request.mode = mode;
    if (mode == Mode::CandidateVeto) {
        request.expectedVisual = g_testSample.visual;
        request.expectedSurface = g_testSample.surface;
        request.expectedBind = g_testSample.bind;
    }
    return request;
}

bool OriginalWasCalled(unsigned int before, LONG result) {
    return result == kNativeResult && g_nativeCandidateCalls == before + 1 && GetLastError() == kNativeError;
}

int g_passed = 0;
int g_failed = 0;
void Check(const char* name, bool okay) {
    if (okay) ++g_passed; else ++g_failed;
    std::printf("%s %s\n", okay ? "PASS" : "FAIL", name);
}

void ExpectPassThrough(const char* name, SharedState* state, Decision expected) {
    const auto before = g_nativeCandidateCalls;
    const auto result = Invoke();
    Check(name, OriginalWasCalled(before, result) && state->telemetry.latest.decision == expected);
}

DWORD WINAPI LifecycleRace(void* value) {
    SetLifecycle(static_cast<Session*>(value), Lifecycle::VetoArmed);
    return 0;
}

void PixelFormatReaderTests() {
    constexpr std::size_t imageBytes = 0x310000;
    auto* image = static_cast<std::uint8_t*>(HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, imageBytes));
    Check("pixel-reader-synthetic-image-allocation", image != nullptr);
    if (!image) return;
    auto* savedBase = g_dwmcore;
    const auto savedSize = g_imageSize;
    g_dwmcore = image;
    g_imageSize = imageBytes;
    alignas(8) std::uint8_t realization[0x80]{};
    alignas(8) std::uint8_t backing[0x180]{};
    auto* sourceTable = reinterpret_cast<void**>(image + 0x2FA1C8);
    auto* surfaceTable = reinterpret_cast<void**>(image + contract::kSurfacePixelFormatVtableRva);
    auto* d2dTable = reinterpret_cast<void**>(image + contract::kD2DBitmapPixelFormatVtableRva);
    *reinterpret_cast<void***>(realization + 8) = sourceTable;
    *reinterpret_cast<void**>(realization + 0x30) = backing;
    sourceTable[3] = image + contract::kRealizationPixelFormatThunkRva;
    surfaceTable[3] = image + contract::kSurfacePixelFormatGetterRva;
    d2dTable[3] = image + contract::kD2DBitmapPixelFormatGetterRva;
    auto* formatThis = backing + 0x50;
    const auto writeField = [formatThis](std::size_t offset, std::uint32_t value) {
        *reinterpret_cast<std::uint32_t*>(formatThis + offset) = value;
    };

    *reinterpret_cast<void***>(formatThis) = surfaceTable;
    writeField(0x24, 87); writeField(0x44, 0);
    writeField(0x50, 10); writeField(0x90, 12);
    Sample sample{};
    Check("D3DSurface-reader-uses-its-own-offsets", ReadPixelFormat(realization, &sample) &&
        sample.pixelFormat == 87 && sample.colorSpace == 0 && (sample.flags & HasPixelFormat));
    writeField(0x44, 12);
    sample = {};
    Check("D3DSurface-HDR-is-read-but-never-classified-SDR", ReadPixelFormat(realization, &sample) &&
        sample.colorSpace == 12 && !IsVerifiedSdrFormat(sample.colorSpace, sample.pixelFormat));

    *reinterpret_cast<void***>(formatThis) = d2dTable;
    writeField(0x24, 10); writeField(0x44, 12);
    writeField(0x50, 87); writeField(0x90, 0);
    sample = {};
    Check("D2DBitmap-reader-uses-distinct-verified-offsets", ReadPixelFormat(realization, &sample) &&
        sample.pixelFormat == 87 && sample.colorSpace == 0 && (sample.flags & HasPixelFormat));
    writeField(0x90, 12);
    sample = {};
    Check("D2DBitmap-PQ-is-read-but-never-classified-SDR", ReadPixelFormat(realization, &sample) &&
        sample.colorSpace == 12 && !IsVerifiedSdrFormat(sample.colorSpace, sample.pixelFormat));
    writeField(0x50, 10); writeField(0x90, 1);
    sample = {};
    Check("D2DBitmap-FP16-scRGB-remains-excluded", ReadPixelFormat(realization, &sample) &&
        sample.pixelFormat == 10 && sample.colorSpace == 1 && !IsVerifiedSdrFormat(sample.colorSpace, sample.pixelFormat));

    d2dTable[3] = image + contract::kSurfacePixelFormatGetterRva;
    sample = {};
    Check("D2DBitmap-table-with-D3DSurface-getter-is-rejected", !ReadPixelFormat(realization, &sample) &&
        !(sample.flags & HasPixelFormat));
    d2dTable[3] = image + contract::kD2DBitmapPixelFormatGetterRva;
    surfaceTable[3] = image + contract::kD2DBitmapPixelFormatGetterRva;
    *reinterpret_cast<void***>(formatThis) = surfaceTable;
    sample = {};
    Check("D3DSurface-table-with-D2DBitmap-getter-is-rejected", !ReadPixelFormat(realization, &sample) &&
        !(sample.flags & HasPixelFormat));
    surfaceTable[3] = image + contract::kSurfacePixelFormatGetterRva;
    *reinterpret_cast<void***>(formatThis) = reinterpret_cast<void**>(image + 0x2F9000);
    sample = {};
    Check("unknown-pixel-format-vtable-is-rejected", !ReadPixelFormat(realization, &sample) &&
        !(sample.flags & HasPixelFormat));
    *reinterpret_cast<void***>(formatThis) = d2dTable;
    sourceTable[3] = image + contract::kRealizationPixelFormatThunkRva + 16;
    sample = {};
    Check("unknown-realization-thunk-is-rejected-before-leaf", !ReadPixelFormat(realization, &sample) &&
        !(sample.flags & HasPixelFormat));
    sourceTable[3] = image + contract::kRealizationPixelFormatThunkRva;
    *reinterpret_cast<void**>(realization + 0x30) = reinterpret_cast<void*>(1);
    sample = {};
    Check("pixel-reader-invalid-backing-is-guarded", !ReadPixelFormat(realization, &sample) &&
        sample.decision == Decision::ReadFault && !(sample.flags & HasPixelFormat));
    g_dwmcore = savedBase;
    g_imageSize = savedSize;
    HeapFree(GetProcessHeap(), 0, image);
}

} // namespace

int main() {
    std::printf("Selective DWM host tests; synthetic functions only; no desktop/game attachment.\n");
    Check("wire-layout", sizeof(Request) == 128 && sizeof(Sample) == 112 && sizeof(SharedState) == 480);
    Capabilities caps{sizeof(Capabilities), kProtocolVersion};
    Check("capabilities-no-forced-independent-flip", SelectiveDwmGetCapabilities(&caps) == 0 &&
        (caps.bits & VetoCandidates) && !(caps.bits & DemoteIndependentFlip));
    StartConfig start{};
    start.size = sizeof(start);
    start.version = kProtocolVersion;
    start.magic = kProtocolMagic;
    start.nonceLow = 1;
    Check("production-start-rejects-non-dwm-host", SelectiveDwmStart(&start) == ERROR_BAD_ENVIRONMENT);
    Check("null-start-rejected", SelectiveDwmStart(nullptr) == ERROR_INVALID_PARAMETER);
    Check("invalid-start-pointer-rejected", SelectiveDwmStart(reinterpret_cast<void*>(1)) == ERROR_NOACCESS);
    wchar_t dllPath[MAX_PATH]{};
    GetModuleFileNameW(nullptr, dllPath, MAX_PATH);
    auto* fileName = std::wcsrchr(dllPath, L'\\');
    if (fileName) wcscpy_s(fileName + 1, static_cast<std::size_t>(MAX_PATH - (fileName + 1 - dllPath)),
                           L"HDRCalibrate.SelectiveDwmHook.dll");
    const auto actualDll = fileName ? LoadLibraryW(dllPath) : nullptr;
    using ExportFunction = DWORD(WINAPI*)(void*);
    const auto actualStart = actualDll ? reinterpret_cast<ExportFunction>(GetProcAddress(actualDll, "SelectiveDwmStart")) : nullptr;
    const auto actualCaps = actualDll ? reinterpret_cast<ExportFunction>(GetProcAddress(actualDll, "SelectiveDwmGetCapabilities")) : nullptr;
    Capabilities actualCapabilities{sizeof(Capabilities), kProtocolVersion};
    Check("actual-production-DLL-load-and-export-ABI", actualCaps && actualCaps(&actualCapabilities) == 0 &&
        actualCapabilities.startConfigSize == 304 && actualCapabilities.sharedStateSize == 480);
    Check("actual-production-DLL-rejects-normal-host-before-hooks", actualStart && actualStart(&start) == ERROR_BAD_ENVIRONMENT);
    // Rejected before pinning/hooks, so this ordinary canary may release its
    // original load reference. This is not production DWM detach logic.
    if (actualDll) FreeLibrary(actualDll);

    HANDLE limitedHandle = nullptr;
    const bool duplicated = DuplicateHandle(GetCurrentProcess(), GetCurrentProcess(), GetCurrentProcess(),
        &limitedHandle, PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, 0) != FALSE;
    FILETIME processStart{}, processExit{}, processKernel{}, processUser{};
    GetProcessTimes(GetCurrentProcess(), &processStart, &processExit, &processKernel, &processUser);
    Check("metadata-only-duplicated-handle-verifies-PID-and-creation", duplicated &&
        VerifyProcess(limitedHandle, GetCurrentProcessId(), FileTimeValue(processStart)) == 0);
    DWORD handleSession = 0, ordinarySession = 0;
    Check("metadata-handle-session-matches-Win32-without-PID-reopen", duplicated &&
        QueryHandleSession(limitedHandle, &handleSession) == 0 &&
        ProcessIdToSessionId(GetCurrentProcessId(), &ordinarySession) && handleSession == ordinarySession);
    Check("metadata-handle-rejects-PID-mismatch", duplicated &&
        VerifyProcess(limitedHandle, GetCurrentProcessId() + 1, FileTimeValue(processStart)) == ERROR_INVALID_OWNER);
    if (limitedHandle) CloseHandle(limitedHandle);
    PixelFormatReaderTests();

    const bool initialized = MH_Initialize() == MH_OK;
    const bool candidateCreated = initialized && MH_CreateHook(reinterpret_cast<void*>(&NativeCandidate),
        reinterpret_cast<void*>(&CandidateHook), reinterpret_cast<void**>(&g_candidateOriginal)) == MH_OK;
    const bool flipCreated = candidateCreated && MH_CreateHook(reinterpret_cast<void*>(&NativeFlip),
        reinterpret_cast<void*>(&IndependentFlipHook), reinterpret_cast<void**>(&g_flipOriginal)) == MH_OK;
    const bool attached = flipCreated && MH_EnableHook(MH_ALL_HOOKS) == MH_OK;
    Check("real-minhook-install-on-synthetic-functions", attached);
    if (!attached) return 1;

    auto before = g_nativeCandidateCalls;
    auto result = Invoke();
    Check("all-13-x64-arguments-and-HRESULT-forwarded", OriginalWasCalled(before, result) &&
        std::memcmp(g_received, kArguments, sizeof(kArguments)) == 0);
    before = g_nativeCandidateCalls;
    result = Invoke(false);
    Check("final-stack-bool-false-forwarded", OriginalWasCalled(before, result) && g_received[12] == 0);
    const auto flipBefore = g_nativeFlipCalls;
    result = g_callFlip(reinterpret_cast<void*>(kArguments[0]), reinterpret_cast<void*>(kArguments[1]), true);
    Check("native-independent-flip-true-forwarded", result == kNativeResult &&
        g_nativeFlipCalls == flipBefore + 1 && g_received[0] == kArguments[0] &&
        g_received[1] == kArguments[1] && g_received[2] == 1);

    LARGE_INTEGER frequency{};
    QueryPerformanceFrequency(&frequency);
    g_frequency = frequency.QuadPart;
    SharedState shared{};
    Session session{};
    session.shared = &shared;
    PublishStartupStage(&session, Stage::StartProcessHandles);
    Check("startup-diagnostic-stage-published-with-valid-sequence", shared.telemetry.latest.sequence > 0 &&
        !(shared.telemetry.latest.sequence & 1) && shared.telemetry.latest.stage == Stage::StartProcessHandles &&
        shared.telemetry.latest.qpc > 0);
    session.controller = GetCurrentProcess();
    session.target = GetCurrentProcess();
    session.identity.magic = kProtocolMagic;
    session.identity.version = kProtocolVersion;
    session.identity.size = sizeof(Request);
    session.identity.nonceLow = 0x1122;
    session.identity.nonceHigh = 0x3344;
    session.identity.controllerPid = GetCurrentProcessId();
    session.identity.targetPid = GetCurrentProcessId();
    session.identity.controllerCreationTime = 12345;
    session.identity.targetCreationTime = 12345;
    session.identity.targetHwnd = 0x11112222;
    session.identity.qpcFrequency = g_frequency;
    g_testSyntheticSample = true;
    g_testSample.visual = 0x10101010;
    g_testSample.surface = 0x20202020;
    g_testSample.bind = 0x30303030;
    g_testSample.hwnd = session.identity.targetHwnd;
    g_testSample.ownerPid = session.identity.targetPid;
    g_testSample.pixelFormat = 87;
    g_testSample.colorSpace = 0;
    g_testSample.flags = OwnershipVerified | HasPixelFormat | HasBindInfo;
    InterlockedExchangePointer(&g_current, &session);

    Publish(&shared, Lease(session, Mode::CandidateVeto));
    ExpectPassThrough("arming-before-observation-passes-through", &shared, Decision::MissingObservation);
    Publish(&shared, Lease(session, Mode::Observe));
    ExpectPassThrough("observer-first-call-passes-through", &shared, Decision::Observe);
    ExpectPassThrough("observer-second-call-passes-through", &shared, Decision::Observe);
    Check("two-observations-do-not-arm", !(shared.telemetry.latestTarget.flags & ObservedBeforeArm));
    ExpectPassThrough("observer-third-call-passes-through", &shared, Decision::Observe);
    Check("three-observations-publish-proof", (shared.telemetry.latestTarget.flags & ObservedBeforeArm) != 0);
    const auto eligibleCandidateSequence = shared.telemetry.latestTarget.sequence;
    g_callFlip(nullptr, nullptr, true);
    Check("Ensure-cannot-overwrite-eligible-candidate-slot", shared.telemetry.latest.point == HookPoint::IndependentFlip &&
        shared.telemetry.latestTarget.point == HookPoint::Candidate &&
        shared.telemetry.latestTarget.sequence == eligibleCandidateSequence &&
        (shared.telemetry.latestTarget.flags & ObservedBeforeArm));

    Publish(&shared, Lease(session, Mode::CandidateVeto));
    before = g_nativeCandidateCalls;
    SetLastError(1234);
    result = Invoke();
    Check("verified-selected-SDR-veto-returns-native-rejection-S_OK", result == 0 &&
        g_nativeCandidateCalls == before && GetLastError() == 1234 && shared.telemetry.vetoedCandidates == 1);
    Check("veto-does-not-overwrite-argument-storage", g_received[12] == 1);

    const auto originalSample = g_testSample;
    ++g_testSample.hwnd;
    ExpectPassThrough("other-window-passes-through", &shared, Decision::DifferentWindow);
    g_testSample = originalSample;
    ++g_testSample.ownerPid;
    ExpectPassThrough("other-process-passes-through", &shared, Decision::DifferentProcess);
    g_testSample = originalSample;
    g_testSample.flags &= ~OwnershipVerified;
    ExpectPassThrough("unknown-owner-passes-through", &shared, Decision::OwnershipUnknown);
    g_testSample = originalSample;
    const std::uint32_t excludedSpaces[] = {1, 9, 12, 13, 18, 19, 0xFFFFFFFFU};
    for (auto colorSpace : excludedSpaces) {
        g_testSample.colorSpace = colorSpace;
        ExpectPassThrough("HDR-linear-widegamut-or-unknown-colorspace-passes-through", &shared, Decision::NotVerifiedSdr);
    }
    g_testSample = originalSample;
    const std::uint32_t excludedFormats[] = {0, 10, 24, 29, 91};
    for (auto pixelFormat : excludedFormats) {
        g_testSample.pixelFormat = pixelFormat;
        ExpectPassThrough("unknown-FP16-10bit-or-sRGB-format-passes-through", &shared, Decision::NotVerifiedSdr);
    }
    g_testSample = originalSample;
    g_testSample.flags &= ~HasBindInfo;
    ExpectPassThrough("unverified-current-bind-passes-through", &shared, Decision::SurfaceChanged);
    g_testSample = originalSample;
    ++g_testSample.bind;
    ExpectPassThrough("changed-current-bind-loses-observation", &shared, Decision::MissingObservation);
    g_testSample = originalSample;

    auto request = Lease(session, Mode::CandidateVeto);
    request.expectedBind = 0;
    Publish(&shared, request);
    ExpectPassThrough("zero-expected-bind-cannot-arm", &shared, Decision::SurfaceChanged);
    request = Lease(session, Mode::CandidateVeto);
    ++request.expectedSurface;
    Publish(&shared, request);
    ExpectPassThrough("different-requested-surface-passes-through", &shared, Decision::SurfaceChanged);
    request = Lease(session, Mode::CandidateVeto);
    request.leaseIssuedQpc -= g_frequency * 6;
    request.leaseDeadlineQpc -= g_frequency * 6;
    Publish(&shared, request);
    ExpectPassThrough("expired-lease-passes-through", &shared, Decision::ExpiredLease);
    request = Lease(session, Mode::CandidateVeto);
    request.leaseIssuedQpc += g_frequency;
    request.leaseDeadlineQpc += g_frequency;
    Publish(&shared, request);
    ExpectPassThrough("future-issued-lease-passes-through", &shared, Decision::InvalidLease);
    request = Lease(session, Mode::CandidateVeto);
    request.leaseDeadlineQpc += g_frequency;
    Publish(&shared, request);
    ExpectPassThrough("lease-over-five-seconds-passes-through", &shared, Decision::InvalidLease);
    request = Lease(session, Mode::CandidateVeto);
    ++request.nonceLow;
    Publish(&shared, request);
    ExpectPassThrough("changed-session-nonce-passes-through", &shared, Decision::IdentityChanged);
    request = Lease(session, Mode::CandidateVeto);
    ++request.targetCreationTime;
    Publish(&shared, request);
    ExpectPassThrough("PID-reuse-creation-time-change-passes-through", &shared, Decision::IdentityChanged);
    Publish(&shared, Lease(session, Mode::CandidateVeto));
    InterlockedIncrement64(Atomic64(&shared.request.sequence));
    ExpectPassThrough("torn-request-passes-through-without-spinning", &shared, Decision::TornRequest);

    Publish(&shared, Lease(session, Mode::CandidateVeto));
    const HANDLE signaled = CreateEventW(nullptr, TRUE, TRUE, nullptr);
    session.controller = signaled;
    ExpectPassThrough("signaled-controller-lifetime-passes-through", &shared, Decision::ControllerExited);
    session.controller = GetCurrentProcess();
    session.target = signaled;
    ExpectPassThrough("signaled-target-lifetime-passes-through", &shared, Decision::TargetExited);
    session.target = GetCurrentProcess();
    CloseHandle(signaled);

    Publish(&shared, Lease(session, Mode::Observe));
    for (int i = 0; i != 3; ++i) g_callFlip(nullptr, nullptr, true);
    Publish(&shared, Lease(session, Mode::CandidateVeto));
    const auto flips = g_nativeFlipCalls;
    result = g_callFlip(nullptr, nullptr, true);
    Check("independent-flip-never-forced-off", result == kNativeResult && g_nativeFlipCalls == flips + 1 &&
        g_received[2] == 1 && shared.telemetry.demotedIndependentFlip == 0 &&
        shared.telemetry.latest.decision == Decision::CapabilityUnavailable);

    g_testSyntheticSample = false;
    Publish(&shared, Lease(session, Mode::Observe));
    ExpectPassThrough("invalid-private-object-read-fault-passes-through", &shared, Decision::ReadFault);
    g_testSyntheticSample = true;
    const auto dropped = shared.telemetry.droppedSamples;
    InterlockedIncrement64(Atomic64(&shared.telemetry.latest.sequence));
    result = Invoke();
    Check("contended-sample-writer-drops-without-blocking", result == kNativeResult && shared.telemetry.droppedSamples > dropped);
    InterlockedIncrement64(Atomic64(&shared.telemetry.latest.sequence));

    Check("stop-detaches-session-without-unloading-code", SelectiveDwmStop(nullptr) == 0 &&
        CurrentSession() == nullptr && session.stopped == 1 && shared.telemetry.lifecycle == Lifecycle::StoppedPinned);
    before = g_nativeCandidateCalls;
    result = Invoke();
    Check("post-stop-hook-remains-safe-pass-through", OriginalWasCalled(before, result));
    Check("no-active-callbacks-after-synchronous-tests", shared.telemetry.activeCallbacks == 0);

    // Deterministic actual two-thread interleaving: pause a nonterminal telemetry
    // writer after reading the old lifecycle, execute real Stop, then resume
    // the stale writer. The terminal state must win the compare/exchange.
    InterlockedExchange(&session.stopped, 0);
    shared.telemetry.lifecycle = Lifecycle::Observing;
    InterlockedExchangePointer(&g_current, &session);
    g_testLifecycleReadEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    g_testLifecycleResumeEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    const HANDLE raceThread = CreateThread(nullptr, 0, &LifecycleRace, &session, 0, nullptr);
    const bool paused = raceThread && WaitForSingleObject(g_testLifecycleReadEvent, 3000) == WAIT_OBJECT_0;
    const auto stopResult = SelectiveDwmStop(nullptr);
    SetEvent(g_testLifecycleResumeEvent);
    const bool resumed = raceThread && WaitForSingleObject(raceThread, 3000) == WAIT_OBJECT_0;
    Check("real-stop-race-preserves-terminal-lifecycle", paused && resumed && stopResult == 0 &&
        CurrentSession() == nullptr && shared.telemetry.lifecycle == Lifecycle::StoppedPinned);
    if (raceThread) CloseHandle(raceThread);
    CloseHandle(g_testLifecycleReadEvent);
    CloseHandle(g_testLifecycleResumeEvent);
    g_testLifecycleReadEvent = nullptr;
    g_testLifecycleResumeEvent = nullptr;

    SharedState secondShared{};
    Session secondSession{};
    secondSession.shared = &secondShared;
    secondSession.controller = GetCurrentProcess();
    secondSession.target = GetCurrentProcess();
    secondSession.identity = session.identity;
    ++secondSession.identity.nonceHigh;
    Publish(&secondShared, Lease(secondSession, Mode::Observe));
    InterlockedExchangePointer(&g_current, &secondSession);
    ExpectPassThrough("new-immutable-session-can-observe-with-old-session-pinned", &secondShared, Decision::Observe);
    Check("old-session-terminal-state-survives-rearm", shared.telemetry.lifecycle == Lifecycle::StoppedPinned);
    SelectiveDwmStop(nullptr);

    // Only this synthetic host removes its own temporary test hooks. Production
    // DWM stop never executes these MinHook detach/uninitialize operations.
    Check("synthetic-host-detach", MH_DisableHook(MH_ALL_HOOKS) == MH_OK && MH_Uninitialize() == MH_OK);
    std::printf("RESULT %d passed; %d failed\n", g_passed, g_failed);
    return g_failed ? 1 : 0;
}
