#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <Windows.h>
#include <Psapi.h>
#include <bcrypt.h>
#include <MinHook.h>
#include <cstdint>
#include <cstring>
#include <cwchar>
#include <limits>
#include "../include/SelectiveDwmPolicy.h"
#include "BuildContract.h"

#pragma comment(lib, "bcrypt.lib")
#pragma comment(lib, "user32.lib")

using namespace selective_dwm;

namespace {

// Public PDB signature, independently demangled with MSVC undname:
// HRESULT COverlayContext::CheckAndRecordOverlayCandidate(CVisual*, RectF const&,
// CCompositionSurfaceInfo*, ISwapChainRealization*, RectF const&, RectF const&,
// RectF const&, CCornerRects const&, CMILMatrix const&, DXGI_MODE_ROTATION,
// unsigned int, bool). References are opaque pointers; no argument is invented.
using CandidateFunction = LONG(__fastcall*)(void*, void*, const void*, void*, void*,
    const void*, const void*, const void*, const void*, const void*, std::uint32_t,
    std::uint32_t, bool);
using IndependentFlipFunction = LONG(__fastcall*)(void*, void*, bool);
using GetHwndFunction = HWND(__fastcall*)(const void*);
using GetPidFunction = DWORD(__fastcall*)(const void*);

struct Observation {
    volatile LONG lock;
    std::uint64_t visual;
    std::uint64_t surface;
    std::uint64_t bind;
    std::int64_t lastQpc;
    std::uint32_t count;
};

struct Session {
    SharedState* shared;
    HANDLE mapping;
    HANDLE controller;
    HANDLE target;
    Request identity;
    volatile LONG stopped;
    Observation candidateObservation;
    Observation flipObservation;
};

inline constexpr LONG kMaximumSessions = 16;
inline constexpr std::uint32_t kCapabilities = ObserveCandidates | ObserveIndependentFlip | VetoCandidates;
// DemoteIndependentFlip remains unavailable: the plane contains current source
// ColorSpace but no independently verified current source DXGI_FORMAT. Reading
// a stale candidate's format must never authorize modification of an HDR plane.
Session* g_sessions[kMaximumSessions]{};
LONG g_sessionCount = 0;
PVOID volatile g_current = nullptr;
volatile LONG g_startLock = 0;
volatile LONG g_hooksInstalled = 0;
std::uint8_t* g_dwmcore = nullptr;
std::size_t g_imageSize = 0;
std::int64_t g_frequency = 0;
CandidateFunction g_candidateOriginal = nullptr;
IndependentFlipFunction g_flipOriginal = nullptr;

#ifdef SELECTIVE_DWM_SELF_TEST
Sample g_testSample{};
bool g_testSyntheticSample = false;
HANDLE g_testLifecycleReadEvent = nullptr;
HANDLE g_testLifecycleResumeEvent = nullptr;
#endif

volatile LONG64* Atomic64(std::int64_t* value) noexcept {
    return reinterpret_cast<volatile LONG64*>(value);
}
void Count(std::int64_t* value) noexcept { InterlockedIncrement64(Atomic64(value)); }
std::int64_t Read64(std::int64_t* value) noexcept { return InterlockedCompareExchange64(Atomic64(value), 0, 0); }
void SetLifecycle(Session* session, Lifecycle value) noexcept {
    auto* state = reinterpret_cast<volatile LONG*>(&session->shared->telemetry.lifecycle);
    if (value == Lifecycle::StoppedPinned) {
        InterlockedExchange(state, static_cast<LONG>(value));
        return;
    }
    const LONG before = InterlockedCompareExchange(state, 0, 0);
    if (before == static_cast<LONG>(Lifecycle::StoppedPinned)) return;
#ifdef SELECTIVE_DWM_SELF_TEST
    if (g_testLifecycleReadEvent) {
        SetEvent(g_testLifecycleReadEvent);
        WaitForSingleObject(g_testLifecycleResumeEvent, 5000);
    }
#endif
    // A late callback must never overwrite Stop's terminal telemetry. One CAS
    // attempt is sufficient; another callback may publish the current state.
    InterlockedCompareExchange(state, static_cast<LONG>(value), before);
}
Session* CurrentSession() noexcept {
    return static_cast<Session*>(InterlockedCompareExchangePointer(&g_current, nullptr, nullptr));
}
std::int64_t Qpc() noexcept { LARGE_INTEGER value{}; QueryPerformanceCounter(&value); return value.QuadPart; }
std::uint64_t FileTimeValue(FILETIME value) noexcept {
    return (static_cast<std::uint64_t>(value.dwHighDateTime) << 32) | value.dwLowDateTime;
}
DWORD VerifyProcess(HANDLE process, DWORD expectedPid, std::uint64_t creation) noexcept {
    const DWORD actualPid = GetProcessId(process);
    if (!actualPid) return GetLastError();
    if (actualPid != expectedPid) return ERROR_INVALID_OWNER;
    FILETIME started{}, exited{}, kernel{}, user{};
    if (!GetProcessTimes(process, &started, &exited, &kernel, &user)) return GetLastError();
    if (FileTimeValue(started) != creation) return ERROR_INVALID_DATA;
    const DWORD status = WaitForSingleObject(process, 0);
    if (status == WAIT_TIMEOUT) return ERROR_SUCCESS;
    return status == WAIT_FAILED ? GetLastError() : ERROR_PROCESS_ABORTED;
}
DWORD QueryHandleSession(HANDLE process, DWORD* sessionId) noexcept {
    // Installed KernelBase!ProcessIdToSessionId queries this native 4-byte
    // ProcessSessionInformation (class 24) after reopening a non-current PID.
    // Reuse the already granted metadata handle and avoid that token-dependent
    // reopen. No process memory or token is accessed.
    using QueryInformation = LONG(NTAPI*)(HANDLE, ULONG, void*, ULONG, ULONG*);
    using StatusToError = ULONG(NTAPI*)(LONG);
    const auto ntdll = GetModuleHandleW(L"ntdll.dll");
    if (!ntdll) return ERROR_MOD_NOT_FOUND;
    const auto query = reinterpret_cast<QueryInformation>(GetProcAddress(ntdll, "NtQueryInformationProcess"));
    const auto errorFromStatus = reinterpret_cast<StatusToError>(GetProcAddress(ntdll, "RtlNtStatusToDosError"));
    if (!query || !errorFromStatus) return ERROR_PROC_NOT_FOUND;
    ULONG result = 0, returned = 0;
    const LONG status = query(process, 24, &result, sizeof(result), &returned);
    if (status < 0) return errorFromStatus(status);
    if (returned != sizeof(result)) return ERROR_BAD_LENGTH;
    *sessionId = result;
    return ERROR_SUCCESS;
}
int ReadExceptionFilter(unsigned int code) noexcept {
    return (code == EXCEPTION_ACCESS_VIOLATION || code == EXCEPTION_IN_PAGE_ERROR)
        ? EXCEPTION_EXECUTE_HANDLER : EXCEPTION_CONTINUE_SEARCH;
}

bool ReadRequest(SharedState* state, Request* result) noexcept {
    for (unsigned int attempt = 0; attempt != 3; ++attempt) {
        const auto before = Read64(&state->request.sequence);
        if (before <= 0 || (before & 1)) continue;
        MemoryBarrier();
        std::memcpy(result, &state->request, sizeof(*result));
        MemoryBarrier();
        if (before == Read64(&state->request.sequence) && result->sequence == before) return true;
    }
    return false;
}

bool InImage(const void* address, std::size_t length = sizeof(void*)) noexcept {
    const auto pointer = reinterpret_cast<std::uintptr_t>(address);
    const auto base = reinterpret_cast<std::uintptr_t>(g_dwmcore);
    return pointer >= base && length <= g_imageSize && pointer - base <= g_imageSize - length;
}
bool CodeEquals(const std::uint8_t* image, std::size_t imageSize,
                std::uint32_t rva, const std::uint8_t* bytes, std::size_t length) noexcept {
    return rva <= imageSize && length <= imageSize - rva &&
        std::memcmp(image + rva, bytes, length) == 0;
}

void PublishSample(Session* session, Sample* destination, const Sample& source) noexcept;
void PublishStage(Session* session, Sample* sample, Stage stage) noexcept {
    sample->stage = stage;
    InterlockedExchange64(Atomic64(&session->shared->telemetry.lastCallbackQpc), sample->qpc);
    PublishSample(session, &session->shared->telemetry.latest, *sample);
}
void PublishStartupStage(Session* session, Stage stage) noexcept {
    Sample sample{};
    sample.qpc = Qpc();
    PublishStage(session, &sample, stage);
}

bool ReadVisualIdentity(void* visual, Sample* sample) noexcept {
    if (!visual) return false;
    __try {
        const auto table = *reinterpret_cast<void***>(visual);
        if (!InImage(table, 0x138)) return false;
        const auto hwndGetter = table[0x130 / sizeof(void*)];
        const auto pidGetter = table[0x90 / sizeof(void*)];
        const bool node = hwndGetter == g_dwmcore + contract::kNodeGetHwndRva &&
                          pidGetter == g_dwmcore + contract::kNodeGetPidRva;
        const bool base = hwndGetter == g_dwmcore + contract::kVisualGetHwndRva &&
                          pidGetter == g_dwmcore + contract::kVisualGetPidRva;
        if (!node && !base) return false;
        // Existing, recognized GetHwnd applies the visual's own eligibility
        // flag before consulting its top-level-window property. No parent walk
        // or unguarded sparse-storage traversal is added by this DLL.
        const auto hwnd = reinterpret_cast<GetHwndFunction>(hwndGetter)(visual);
        const auto nativePid = reinterpret_cast<GetPidFunction>(pidGetter)(visual);
        if (!hwnd || !nativePid) return false;
        DWORD windowPid = 0;
        if (!GetWindowThreadProcessId(hwnd, &windowPid) || windowPid != nativePid) return false;
        sample->hwnd = reinterpret_cast<std::uint64_t>(hwnd);
        sample->ownerPid = nativePid;
        sample->flags |= OwnershipVerified;
        if (node) {
            sample->windowNodeHwnd = sample->hwnd;
            sample->windowNodePid = nativePid;
            sample->flags |= HasWindowNodeIdentity;
        } else {
            sample->topLevelProperty = sample->hwnd;
            sample->flags |= HasTopLevelProperty;
        }
        return true;
    } __except (ReadExceptionFilter(GetExceptionCode())) {
        sample->decision = Decision::ReadFault;
        return false;
    }
}

bool ReadPixelFormat(void* realization, Sample* sample) noexcept {
    if (!realization) return false;
    __try {
        auto* realizationBytes = static_cast<std::uint8_t*>(realization);
        const auto sourceTable = *reinterpret_cast<void***>(realizationBytes + 8);
        if (!InImage(sourceTable, 0x20) || sourceTable[3] !=
            g_dwmcore + contract::kRealizationPixelFormatThunkRva) return false;
        // The verified thunk subtracts 0x100 from interface-this (=realization+8).
        // Its native object+0x128 is therefore realization+0x30. Its nested leaf
        // is accepted only for an exact, verified concrete vtable/getter pair.
        auto* surface = *reinterpret_cast<std::uint8_t**>(realizationBytes + 0x30);
        if (!surface) return false;
        auto* formatInterface = surface + 0x50;
        const auto formatTable = *reinterpret_cast<void***>(formatInterface);
        std::uint32_t pixelFormatOffset = 0, colorSpaceOffset = 0;
        if (formatTable == reinterpret_cast<void**>(g_dwmcore + contract::kSurfacePixelFormatVtableRva) &&
            formatTable[3] == g_dwmcore + contract::kSurfacePixelFormatGetterRva) {
            pixelFormatOffset = 0x24;
            colorSpaceOffset = 0x44;
        } else if (formatTable == reinterpret_cast<void**>(g_dwmcore + contract::kD2DBitmapPixelFormatVtableRva) &&
            formatTable[3] == g_dwmcore + contract::kD2DBitmapPixelFormatGetterRva) {
            pixelFormatOffset = 0x50;
            colorSpaceOffset = 0x90;
        } else {
            return false;
        }
        // Mirror the direct reads in the verified 21/24-byte leaves. Do not
        // invoke the outer getter, because its other concrete virtuals are not
        // all known. The source metadata is freshly read for this callback.
        sample->pixelFormat = *reinterpret_cast<std::uint32_t*>(formatInterface + pixelFormatOffset);
        sample->colorSpace = *reinterpret_cast<std::uint32_t*>(formatInterface + colorSpaceOffset);
        sample->flags |= HasPixelFormat;
        return true;
    } __except (ReadExceptionFilter(GetExceptionCode())) {
        sample->decision = Decision::ReadFault;
        return false;
    }
}

bool ReadCurrentSurface(void* surface, void* expectedRealization, Sample* sample) noexcept {
    if (!surface) return false;
    __try {
        auto* bytes = static_cast<std::uint8_t*>(surface);
        const auto table = *reinterpret_cast<void***>(surface);
        if (table != reinterpret_cast<void**>(g_dwmcore + contract::kGlobalCompositionSurfaceVtableRva) ||
            table[0x38 / 8] != g_dwmcore + contract::kSurfaceGetRealizationRva ||
            table[0x40 / 8] != g_dwmcore + contract::kSurfaceGetBindRva) return false;
        const auto realization = *reinterpret_cast<void**>(bytes + 0xD0);
        const auto bind = *reinterpret_cast<std::uint64_t*>(bytes + 0x88);
        if (!realization || !bind || (expectedRealization && realization != expectedRealization) ||
            (sample->bind && sample->bind != bind)) return false;
        sample->realization = reinterpret_cast<std::uint64_t>(realization);
        sample->bind = bind;
        sample->flags |= HasBindInfo;
        return true;
    } __except (ReadExceptionFilter(GetExceptionCode())) {
        sample->decision = Decision::ReadFault;
        return false;
    }
}

bool ReadPlane(Session* session, void* plane, Sample* sample) noexcept {
    if (!plane) return false;
    __try {
        auto* bytes = static_cast<std::uint8_t*>(plane);
        sample->visual = *reinterpret_cast<std::uint64_t*>(bytes + 8);
        sample->surface = *reinterpret_cast<std::uint64_t*>(bytes + 0x10);
        sample->bind = *reinterpret_cast<std::uint64_t*>(bytes + 0x18);
        // A plane must carry its own current bind identity. Zero is not a
        // request to adopt the backing surface's bind (the candidate reader
        // legitimately has no plane bind yet). Missing evidence fails closed.
        if (!sample->bind || !sample->surface || *reinterpret_cast<void**>(sample->surface) !=
            g_dwmcore + contract::kGlobalCompositionSurfaceVtableRva) return false;
        // OverlayPlaneInfo copies the 0x70-byte attributes to +0x20; the source
        // ColorSpace field within those attributes is +0x4C.
        const auto planeColorSpace = *reinterpret_cast<std::uint32_t*>(bytes + 0x6C);
        sample->colorSpace = planeColorSpace;
        PublishStage(session, sample, Stage::SurfaceBinding);
        if (ReadCurrentSurface(reinterpret_cast<void*>(sample->surface), nullptr, sample)) {
            PublishStage(session, sample, Stage::PixelMetadata);
            ReadPixelFormat(reinterpret_cast<void*>(sample->realization), sample);
            if (sample->colorSpace != planeColorSpace) sample->flags &= ~(HasPixelFormat | HasBindInfo);
        }
        PublishStage(session, sample, Stage::WindowIdentity);
        return ReadVisualIdentity(reinterpret_cast<void*>(sample->visual), sample);
    } __except (ReadExceptionFilter(GetExceptionCode())) {
        sample->decision = Decision::ReadFault;
        return false;
    }
}

void PublishSample(Session* session, Sample* destination, const Sample& source) noexcept {
    const auto before = Read64(&destination->sequence);
    if ((before & 1) || before > (std::numeric_limits<std::int64_t>::max)() - 2 ||
        InterlockedCompareExchange64(Atomic64(&destination->sequence), before + 1, before) != before) {
        Count(&session->shared->telemetry.droppedSamples);
        return;
    }
    std::memcpy(reinterpret_cast<std::uint8_t*>(destination) + sizeof(destination->sequence),
                reinterpret_cast<const std::uint8_t*>(&source) + sizeof(source.sequence),
                sizeof(Sample) - sizeof(source.sequence));
    MemoryBarrier();
    InterlockedExchange64(Atomic64(&destination->sequence), before + 2);
}

bool UpdateObservation(Observation* observation, const Sample& sample, bool mayObserve) noexcept {
    if (InterlockedCompareExchange(&observation->lock, 1, 0) != 0) return false;
    bool same = observation->visual == sample.visual && observation->surface == sample.surface &&
                observation->bind == sample.bind;
    if (mayObserve) {
        if (!same) {
            observation->visual = sample.visual;
            observation->surface = sample.surface;
            observation->bind = sample.bind;
            observation->count = 0;
        }
        if (observation->count < 3) ++observation->count;
        observation->lastQpc = sample.qpc;
        same = true;
    }
    const bool established = same && observation->count >= 3;
    InterlockedExchange(&observation->lock, 0);
    return established;
}

Decision Probe(Session* session, Sample* sample, void* visual, void* surface,
               void* realization, void* plane) noexcept {
    if (InterlockedCompareExchange(&session->stopped, 0, 0)) return Decision::Stopped;
    Request request{};
    if (!ReadRequest(session->shared, &request)) return Decision::TornRequest;
    const auto lease = ValidateLease(request, session->identity, sample->qpc, g_frequency);
    if (lease != Decision::Observe) return lease;
    if (WaitForSingleObject(session->controller, 0) != WAIT_TIMEOUT) return Decision::ControllerExited;
    if (WaitForSingleObject(session->target, 0) != WAIT_TIMEOUT) return Decision::TargetExited;
    InterlockedExchange64(Atomic64(&session->shared->telemetry.acceptedRequestSequence), request.sequence);

#ifdef SELECTIVE_DWM_SELF_TEST
    if (g_testSyntheticSample) {
        const auto point = sample->point;
        const auto now = sample->qpc;
        *sample = g_testSample;
        sample->point = point;
        sample->qpc = now;
    } else
#endif
    if (sample->point == HookPoint::Candidate) {
        sample->visual = reinterpret_cast<std::uint64_t>(visual);
        sample->surface = reinterpret_cast<std::uint64_t>(surface);
        sample->realization = reinterpret_cast<std::uint64_t>(realization);
        PublishStage(session, sample, Stage::WindowIdentity);
        ReadVisualIdentity(visual, sample);
        PublishStage(session, sample, Stage::PixelMetadata);
        ReadPixelFormat(realization, sample);
        PublishStage(session, sample, Stage::SurfaceBinding);
        ReadCurrentSurface(surface, realization, sample);
    } else {
        ReadPlane(session, plane, sample);
    }
    PublishStage(session, sample, Stage::Policy);
    if (sample->decision == Decision::ReadFault) return Decision::ReadFault;
    const bool selected = (sample->flags & OwnershipVerified) && sample->hwnd == request.targetHwnd &&
                          sample->ownerPid == request.targetPid;
    if (selected) Count(&session->shared->telemetry.targetMatches);
    const bool sdr = selected && (sample->flags & HasPixelFormat) && (sample->flags & HasBindInfo) &&
                     IsVerifiedSdrFormat(sample->colorSpace, sample->pixelFormat);
    if (sdr) Count(&session->shared->telemetry.verifiedSdrMatches);
    auto* observation = sample->point == HookPoint::Candidate ?
        &session->candidateObservation : &session->flipObservation;
    const bool observed = UpdateObservation(observation, *sample, sdr && request.mode == Mode::Observe);
    if (observed) sample->flags |= ObservedBeforeArm;
    auto decision = EvaluateCandidate(request, *sample, observed, kCapabilities);
    // Stop/expiry is rechecked immediately before returning authorization. No
    // cached enable bit can outlive the lease. No explicit rollback setter is
    // needed because each decision only declines a current native candidate.
    if (decision == Decision::Veto) {
        if (InterlockedCompareExchange(&session->stopped, 0, 0) || CurrentSession() != session)
            return Decision::Stopped;
        if (Qpc() >= request.leaseDeadlineQpc) return Decision::ExpiredLease;
    }
    return decision;
}

void Record(Session* session, Sample* sample) noexcept {
    auto* telemetry = &session->shared->telemetry;
    sample->stage = Stage::Complete;
    if (sample->decision == Decision::ExpiredLease) Count(&telemetry->expiredLeaseCalls);
    if (sample->decision == Decision::ReadFault) Count(&telemetry->readFaults);
    InterlockedExchange64(Atomic64(&telemetry->lastCallbackQpc), sample->qpc);
    PublishSample(session, &telemetry->latest, *sample);
    if (sample->point == HookPoint::Candidate && (sample->flags & OwnershipVerified) && sample->hwnd == session->identity.targetHwnd &&
        sample->ownerPid == session->identity.targetPid) PublishSample(session, &telemetry->latestTarget, *sample);
    if (!InterlockedCompareExchange(&session->stopped, 0, 0)) {
        SetLifecycle(session, sample->decision == Decision::Veto ? Lifecycle::VetoArmed :
                     sample->decision == Decision::Observe ? Lifecycle::Observing : Lifecycle::PassThrough);
    }
}

LONG __fastcall CandidateHook(void* self, void* visual, const void* rect0, void* surface,
    void* realization, const void* rect1, const void* rect2, const void* rect3,
    const void* corners, const void* matrix, std::uint32_t rotation,
    std::uint32_t flags, bool option) {
    const DWORD incomingError = GetLastError();
    auto* session = CurrentSession();
    if (!session) return g_candidateOriginal(self, visual, rect0, surface, realization,
        rect1, rect2, rect3, corners, matrix, rotation, flags, option);
    auto* telemetry = &session->shared->telemetry;
    Count(&telemetry->candidateCalls);
    Count(&telemetry->activeCallbacks);
    Sample sample{};
    sample.qpc = Qpc();
    sample.point = HookPoint::Candidate;
    sample.colorSpace = kUnknownColorSpace;
    sample.visual = reinterpret_cast<std::uint64_t>(visual);
    sample.surface = reinterpret_cast<std::uint64_t>(surface);
    sample.realization = reinterpret_cast<std::uint64_t>(realization);
    PublishStage(session, &sample, Stage::Entered);
    sample.decision = Probe(session, &sample, visual, surface, realization, nullptr);
    SetLastError(incomingError);
    LONG result;
    if (sample.decision == Decision::Veto) {
        // The native function's ordinary rejected-candidate branch
        // (8875: 0x2680FA; 9278: 0x265C3F)
        // returns S_OK without recording a plane. Its public ABI has no output
        // parameters. Returning zero here matches that documented disassembly.
        result = 0;
        Count(&telemetry->vetoedCandidates);
    } else {
        Count(&telemetry->passedThrough);
        PublishStage(session, &sample, Stage::NativeOriginal);
        result = g_candidateOriginal(self, visual, rect0, surface, realization,
            rect1, rect2, rect3, corners, matrix, rotation, flags, option);
    }
    const DWORD resultingError = GetLastError();
    sample.nativeResult = result;
    Record(session, &sample);
    InterlockedDecrement64(Atomic64(&telemetry->activeCallbacks));
    SetLastError(resultingError);
    return result;
}

LONG __fastcall IndependentFlipHook(void* self, void* plane, bool enable) {
    const DWORD incomingError = GetLastError();
    auto* session = CurrentSession();
    if (!session) return g_flipOriginal(self, plane, enable);
    auto* telemetry = &session->shared->telemetry;
    Count(&telemetry->independentFlipCalls);
    Count(&telemetry->activeCallbacks);
    Sample sample{};
    sample.qpc = Qpc();
    sample.point = HookPoint::IndependentFlip;
    sample.colorSpace = kUnknownColorSpace;
    sample.originalEnable = enable ? 1U : 0U;
    PublishStage(session, &sample, Stage::Entered);
    sample.decision = Probe(session, &sample, nullptr, nullptr, nullptr, plane);
    // Observation only until fresh complete source metadata is proven here.
    // In particular this never invokes SurfaceInfo::DisableIndependentFlip.
    SetLastError(incomingError);
    Count(&telemetry->passedThrough);
    PublishStage(session, &sample, Stage::NativeOriginal);
    const LONG result = g_flipOriginal(self, plane, enable);
    const DWORD resultingError = GetLastError();
    sample.nativeResult = result;
    Record(session, &sample);
    InterlockedDecrement64(Atomic64(&telemetry->activeCallbacks));
    SetLastError(resultingError);
    return result;
}

bool Sha256File(const wchar_t* path, std::uint8_t output[32]) noexcept {
    HANDLE file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_DELETE,
        nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;
    LARGE_INTEGER length{};
    bool okay = GetFileSizeEx(file, &length) && length.QuadPart > 0 && length.QuadPart <= 128LL * 1024 * 1024;
    BCRYPT_ALG_HANDLE algorithm = nullptr;
    BCRYPT_HASH_HANDLE hash = nullptr;
    if (okay) okay = BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) >= 0;
    if (okay) okay = BCryptCreateHash(algorithm, &hash, nullptr, 0, nullptr, 0, 0) >= 0;
    std::uint8_t buffer[16384];
    DWORD read = 0;
    std::int64_t total = 0;
    while (okay && total < length.QuadPart) {
        okay = ReadFile(file, buffer, sizeof(buffer), &read, nullptr) && read != 0;
        if (okay) { total += read; okay = BCryptHashData(hash, buffer, read, 0) >= 0; }
    }
    if (okay) okay = total == length.QuadPart && BCryptFinishHash(hash, output, 32, 0) >= 0;
    if (hash) BCryptDestroyHash(hash);
    if (algorithm) BCryptCloseAlgorithmProvider(algorithm, 0);
    CloseHandle(file);
    return okay;
}

bool ExactHost() noexcept {
    wchar_t executable[MAX_PATH]{};
    wchar_t expected[MAX_PATH]{};
    const DWORD actualLength = GetModuleFileNameW(nullptr, executable, MAX_PATH);
    const UINT expectedLength = GetSystemDirectoryW(expected, MAX_PATH);
    return actualLength && actualLength < MAX_PATH && expectedLength && expectedLength < MAX_PATH - 10 &&
        wcscat_s(expected, L"\\dwm.exe") == 0 && _wcsicmp(executable, expected) == 0;
}

// The exact same bounded mapped-image check is exercised by host tests using
// their own memory. It neither loads a module nor publishes global pointers.
// The production caller still requires the exact system path and file SHA.
bool VerifyMappedImageContract(const std::uint8_t* image, std::size_t mappedSize) noexcept {
    using namespace contract;
    if (!image || mappedSize < sizeof(IMAGE_DOS_HEADER) || mappedSize > 128u * 1024u * 1024u)
        return false;
    __try {
        const auto dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(image);
        if (dos->e_magic != IMAGE_DOS_SIGNATURE || dos->e_lfanew <= 0 || dos->e_lfanew > 0x100000)
            return false;
        const auto ntOffset = static_cast<std::size_t>(dos->e_lfanew);
        if (ntOffset > mappedSize || sizeof(IMAGE_NT_HEADERS64) > mappedSize - ntOffset)
            return false;
        const auto nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(image + ntOffset);
        if (nt->Signature != IMAGE_NT_SIGNATURE || nt->FileHeader.Machine != IMAGE_FILE_MACHINE_AMD64 ||
            nt->FileHeader.SizeOfOptionalHeader != sizeof(IMAGE_OPTIONAL_HEADER64) ||
            nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR64_MAGIC ||
            nt->OptionalHeader.SizeOfImage != mappedSize)
            return false;
#if HDR_SELECTIVE_DWMCORE_BUILD == 261009278
        if (mappedSize != kMappedImageSize) return false;
#endif
#define CHECK_CODE(name, rva) CodeEquals(image, mappedSize, rva, name, sizeof(name))
        const bool baseContract = CHECK_CODE(kCandidatePrologue, kCandidateRva) &&
            CHECK_CODE(kIndependentFlipPrologue, kIndependentFlipRva) &&
            CHECK_CODE(kVisualGetHwnd, kVisualGetHwndRva) && CHECK_CODE(kNodeGetHwnd, kNodeGetHwndRva) &&
            CHECK_CODE(kVisualGetPid, kVisualGetPidRva) && CHECK_CODE(kNodeGetPid, kNodeGetPidRva) &&
            CHECK_CODE(kTopLevelWindow, kTopLevelWindowRva) &&
            CHECK_CODE(kRealizationPixelFormatThunk, kRealizationPixelFormatThunkRva) &&
            CHECK_CODE(kSurfacePixelFormatGetter, kSurfacePixelFormatGetterRva) &&
            CHECK_CODE(kD2DBitmapPixelFormatGetter, kD2DBitmapPixelFormatGetterRva) &&
            CHECK_CODE(kSurfaceGetRealization, kSurfaceGetRealizationRva) &&
            CHECK_CODE(kSurfaceGetBind, kSurfaceGetBindRva);
#if HDR_SELECTIVE_DWMCORE_BUILD == 261009278
        return baseContract && CHECK_CODE(kPixelFormatOuter, kPixelFormatOuterRva) &&
            CHECK_CODE(kPlaneConstructor, kPlaneConstructorRva);
#else
        return baseContract;
#endif
#undef CHECK_CODE
    } __except (ReadExceptionFilter(GetExceptionCode())) {
        return false;
    }
}

bool VerifyContract() noexcept {
    using namespace contract;
    const auto module = GetModuleHandleW(L"dwmcore.dll");
    if (!module) return false;
    wchar_t actual[MAX_PATH]{};
    wchar_t expected[MAX_PATH]{};
    const DWORD actualLength = GetModuleFileNameW(module, actual, MAX_PATH);
    const UINT expectedLength = GetSystemDirectoryW(expected, MAX_PATH);
    if (!actualLength || actualLength >= MAX_PATH || !expectedLength || expectedLength >= MAX_PATH - 14 ||
        wcscat_s(expected, L"\\dwmcore.dll") || _wcsicmp(actual, expected)) return false;
    std::uint8_t hash[32]{};
    if (!Sha256File(actual, hash) || std::memcmp(hash, kDwmcoreSha256, sizeof(hash))) return false;
    MODULEINFO information{};
    if (!K32GetModuleInformation(GetCurrentProcess(), module, &information, sizeof(information)) ||
        information.lpBaseOfDll != module ||
        !VerifyMappedImageContract(reinterpret_cast<const std::uint8_t*>(module), information.SizeOfImage))
        return false;
    g_dwmcore = reinterpret_cast<std::uint8_t*>(module);
    g_imageSize = information.SizeOfImage;
    return true;
}

DWORD InstallHooks(Session* session) noexcept {
    if (InterlockedCompareExchange(&g_hooksInstalled, 0, 0)) return ERROR_SUCCESS;
    if (!VerifyContract()) return ERROR_REVISION_MISMATCH;
    PublishStartupStage(session, Stage::StartHookInstallation);
    HMODULE pinned = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
        reinterpret_cast<LPCWSTR>(&InstallHooks), &pinned)) return GetLastError();
    auto status = MH_Initialize();
    if (status != MH_OK) return ERROR_INVALID_FUNCTION;
    status = MH_CreateHook(g_dwmcore + contract::kCandidateRva, reinterpret_cast<void*>(&CandidateHook),
                          reinterpret_cast<void**>(&g_candidateOriginal));
    if (status != MH_OK) return ERROR_INVALID_FUNCTION;
    status = MH_CreateHook(g_dwmcore + contract::kIndependentFlipRva, reinterpret_cast<void*>(&IndependentFlipHook),
                          reinterpret_cast<void**>(&g_flipOriginal));
    if (status != MH_OK) return ERROR_INVALID_FUNCTION;
    status = MH_QueueEnableHook(g_dwmcore + contract::kCandidateRva);
    if (status == MH_OK) status = MH_QueueEnableHook(g_dwmcore + contract::kIndependentFlipRva);
    if (status == MH_OK) status = MH_ApplyQueued();
    if (status != MH_OK) return ERROR_INVALID_FUNCTION;
    InterlockedExchange(&g_hooksInstalled, 1);
    return ERROR_SUCCESS;
}

void DestroyUnpublishedSession(Session* session) noexcept {
    if (!session) return;
    if (session->controller) CloseHandle(session->controller);
    if (session->target) CloseHandle(session->target);
    if (session->shared) UnmapViewOfFile(session->shared);
    if (session->mapping) CloseHandle(session->mapping);
    HeapFree(GetProcessHeap(), 0, session);
}

DWORD Start(const StartConfig& config) noexcept {
    if (!ExactHost()) return ERROR_BAD_ENVIRONMENT;
    if (config.size != sizeof(config) || config.version != kProtocolVersion || config.magic != kProtocolMagic ||
        (!config.nonceLow && !config.nonceHigh)) return ERROR_INVALID_PARAMETER;
    const auto name = reinterpret_cast<const wchar_t*>(config.mappingName);
    constexpr wchar_t prefix[] = L"Local\\HDRCalibrate.SelectiveDwm.";
    if (config.mappingName[kMappingNameCharacters - 1] ||
        wcsncmp(name, prefix, (sizeof(prefix) / sizeof(prefix[0])) - 1) != 0) return ERROR_INVALID_NAME;
    if (CurrentSession()) return ERROR_ALREADY_EXISTS;
    if (g_sessionCount >= kMaximumSessions) return ERROR_TOO_MANY_SESS;
    auto* session = static_cast<Session*>(HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(Session)));
    if (!session) return ERROR_OUTOFMEMORY;
    session->mapping = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, name);
    if (!session->mapping) { const auto error = GetLastError(); DestroyUnpublishedSession(session); return error; }
    session->shared = static_cast<SharedState*>(MapViewOfFile(session->mapping,
        FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(SharedState)));
    if (!session->shared) { const auto error = GetLastError(); DestroyUnpublishedSession(session); return error; }
    LARGE_INTEGER frequency{};
    QueryPerformanceFrequency(&frequency);
    if (!g_frequency) g_frequency = frequency.QuadPart;
    if (g_frequency != frequency.QuadPart) { DestroyUnpublishedSession(session); return ERROR_INVALID_DATA; }
    Request request{};
    if (!ReadRequest(session->shared, &request) || request.nonceLow != config.nonceLow ||
        request.nonceHigh != config.nonceHigh || !request.controllerPid || !request.targetPid ||
        !request.controllerCreationTime || !request.targetCreationTime || !request.targetHwnd ||
        request.mode != Mode::Observe || request.expectedVisual || request.expectedSurface || request.expectedBind ||
        ValidateLease(request, request, Qpc(), g_frequency) != Decision::Observe) {
        DestroyUnpublishedSession(session); return ERROR_INVALID_DATA;
    }
    session->identity = request;
    std::memset(&session->shared->telemetry, 0, sizeof(Telemetry));
    auto* telemetry = &session->shared->telemetry;
    telemetry->size = sizeof(Telemetry);
    telemetry->version = kProtocolVersion;
    telemetry->capabilities = kCapabilities;
    telemetry->hostPid = GetCurrentProcessId();
    telemetry->lifecycle = Lifecycle::Starting;
    PublishStartupStage(session, Stage::StartProcessHandles);
    const auto fail = [session](DWORD error) noexcept {
        session->shared->telemetry.lastWin32Error = error;
        session->shared->telemetry.lifecycle = Lifecycle::Failed;
        DestroyUnpublishedSession(session);
        return error;
    };
    if (!config.controllerProcessHandle || !config.targetProcessHandle ||
        config.controllerProcessHandle == static_cast<std::uint64_t>(-1LL) ||
        config.targetProcessHandle == static_cast<std::uint64_t>(-1LL)) return fail(ERROR_INVALID_HANDLE);
    // Do not OpenProcess from DWM's token, enable privileges, or borrow a game
    // memory-access handle. The controller supplied exactly the metadata and
    // liveness rights needed. Create independently owned local duplicates, so
    // both successful and failed starts have unambiguous handle ownership.
    if (!DuplicateHandle(GetCurrentProcess(), reinterpret_cast<HANDLE>(config.controllerProcessHandle),
        GetCurrentProcess(), &session->controller, PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, 0))
        return fail(GetLastError());
    if (!DuplicateHandle(GetCurrentProcess(), reinterpret_cast<HANDLE>(config.targetProcessHandle),
        GetCurrentProcess(), &session->target, PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, 0))
        return fail(GetLastError());
    PublishStartupStage(session, Stage::StartProcessIdentity);
    DWORD identityError = VerifyProcess(session->controller, request.controllerPid, request.controllerCreationTime);
    if (!identityError) identityError = VerifyProcess(session->target, request.targetPid, request.targetCreationTime);
    if (identityError) return fail(identityError);
    PublishStartupStage(session, Stage::StartWindowIdentity);
    DWORD ownerPid = 0;
    const auto hwnd = reinterpret_cast<HWND>(request.targetHwnd);
    if (!GetWindowThreadProcessId(hwnd, &ownerPid)) return fail(ERROR_INVALID_WINDOW_HANDLE);
    if (ownerPid != request.targetPid) return fail(ERROR_INVALID_OWNER);
    PublishStartupStage(session, Stage::StartSessionIdentity);
    DWORD targetSession = 0, hostSession = 0, controllerSession = 0;
    if (!ProcessIdToSessionId(GetCurrentProcessId(), &hostSession)) return fail(GetLastError());
    identityError = QueryHandleSession(session->target, &targetSession);
    if (!identityError) identityError = QueryHandleSession(session->controller, &controllerSession);
    if (identityError) return fail(identityError);
    if (hostSession != targetSession || hostSession != controllerSession) return fail(ERROR_INVALID_OWNER);
    PublishStartupStage(session, Stage::StartBinaryContract);
    const auto error = InstallHooks(session);
    if (error != ERROR_SUCCESS) {
        telemetry->lastWin32Error = error;
        telemetry->lifecycle = Lifecycle::Failed;
        DestroyUnpublishedSession(session);
        return error;
    }
    g_sessions[g_sessionCount++] = session;
    SetLifecycle(session, Lifecycle::Observing);
    InterlockedExchangePointer(&g_current, session);
    return ERROR_SUCCESS;
}

} // namespace

extern "C" __declspec(dllexport) DWORD WINAPI SelectiveDwmGetCapabilities(void* argument) noexcept {
    if (!argument) return ERROR_INVALID_PARAMETER;
    __try {
        auto* capabilities = static_cast<Capabilities*>(argument);
        if (capabilities->size != sizeof(Capabilities) || capabilities->version != kProtocolVersion)
            return ERROR_REVISION_MISMATCH;
        *capabilities = { sizeof(Capabilities), kProtocolVersion, kCapabilities,
            kMaximumLeaseMilliseconds, sizeof(SharedState), sizeof(StartConfig), 1, 1 };
        return ERROR_SUCCESS;
    } __except (ReadExceptionFilter(GetExceptionCode())) { return ERROR_NOACCESS; }
}

extern "C" __declspec(dllexport) DWORD WINAPI SelectiveDwmStart(void* argument) noexcept {
    if (!argument) return ERROR_INVALID_PARAMETER;
    StartConfig config{};
    __try { std::memcpy(&config, argument, sizeof(config)); }
    __except (ReadExceptionFilter(GetExceptionCode())) { return ERROR_NOACCESS; }
    if (InterlockedCompareExchange(&g_startLock, 1, 0)) return ERROR_BUSY;
    const DWORD result = Start(config);
    InterlockedExchange(&g_startLock, 0);
    return result;
}

extern "C" __declspec(dllexport) DWORD WINAPI SelectiveDwmStop(void*) noexcept {
    if (InterlockedCompareExchange(&g_startLock, 1, 0)) return ERROR_BUSY;
    auto* session = CurrentSession();
    if (session) {
        InterlockedExchange(&session->stopped, 1);
        InterlockedExchangePointer(&g_current, nullptr);
        SetLifecycle(session, Lifecycle::StoppedPinned);
    }
    InterlockedExchange(&g_startLock, 0);
    return ERROR_SUCCESS;
}

#ifndef SELECTIVE_DWM_SELF_TEST
BOOL WINAPI DllMain(HINSTANCE, DWORD, LPVOID) { return TRUE; }
#endif
