#pragma once

// Versioned, x64 Windows wire protocol. The controller owns Request; the DLL owns
// Telemetry. Publish each request by making sequence odd, writing all fields,
// issuing a full memory barrier, then publishing the next even sequence.
// Readers never spin waiting for a writer. A torn request means pass-through.
#include <cstddef>
#include <cstdint>
#include <type_traits>

namespace selective_dwm {

inline constexpr std::uint32_t kProtocolVersion = 2;
inline constexpr std::uint64_t kProtocolMagic = 0x31574D44534C4553ULL; // SELSDMW1
inline constexpr std::uint32_t kMaximumLeaseMilliseconds = 5000;
inline constexpr std::uint32_t kMappingNameCharacters = 128;
inline constexpr std::uint32_t kUnknownColorSpace = 0xFFFFFFFFU;

enum class Mode : std::uint32_t { Observe = 0, CandidateVeto = 1 };
enum class Lifecycle : std::uint32_t {
    Dormant = 0, Starting = 1, Observing = 2, VetoArmed = 3,
    PassThrough = 4, StoppedPinned = 5, Failed = 6
};
enum Capability : std::uint32_t {
    ObserveCandidates = 1U << 0,
    ObserveIndependentFlip = 1U << 1,
    VetoCandidates = 1U << 2,
    DemoteIndependentFlip = 1U << 3,
    // Neither VRR acceptance nor correct gamma/HDR rendering is a DLL capability.
};
enum class HookPoint : std::uint32_t { None = 0, Candidate = 1, IndependentFlip = 2 };
enum class Stage : std::uint32_t {
    None = 0, Entered = 1, WindowIdentity = 2, PixelMetadata = 3,
    SurfaceBinding = 4, Policy = 5, NativeOriginal = 6, Complete = 7,
    StartProcessHandles = 8, StartProcessIdentity = 9, StartWindowIdentity = 10,
    StartSessionIdentity = 11, StartBinaryContract = 12, StartHookInstallation = 13
};
enum class Decision : std::uint32_t {
    Observe = 0, Veto = 1, Stopped = 2, InvalidProtocol = 3,
    TornRequest = 4, IdentityChanged = 5, InvalidLease = 6, ExpiredLease = 7,
    ControllerExited = 8, TargetExited = 9, OwnershipUnknown = 10,
    DifferentWindow = 11, DifferentProcess = 12, NotVerifiedSdr = 13,
    MissingObservation = 14, SurfaceChanged = 15, CapabilityUnavailable = 16,
    ReadFault = 17, Reentrant = 18, NativeAlreadyDisabled = 19
};
enum SampleFlag : std::uint32_t {
    HasTopLevelProperty = 1U << 0,
    HasWindowNodeIdentity = 1U << 1,
    HasPixelFormat = 1U << 2,
    HasBindInfo = 1U << 3,
    // Historical wire name above: this is the current uint64 BindId returned
    // by GetCurrentBindId, never a dereferenceable CBindInfo object pointer.
    HasCurrentBindId = HasBindInfo,
    OwnershipVerified = 1U << 4,
    ObservedBeforeArm = 1U << 5,
};

struct alignas(8) Request {
    std::int64_t sequence;
    std::uint64_t magic;
    std::uint32_t version;
    std::uint32_t size;
    std::uint64_t nonceLow;
    std::uint64_t nonceHigh;
    std::uint32_t controllerPid;
    std::uint32_t targetPid;
    std::uint64_t controllerCreationTime;
    std::uint64_t targetCreationTime;
    std::uint64_t targetHwnd;
    std::int64_t qpcFrequency;
    std::int64_t leaseIssuedQpc;
    std::int64_t leaseDeadlineQpc;
    Mode mode;
    std::uint32_t reserved;
    // Arm only a tuple returned by the observer in this DLL session. Candidate
    // callbacks verify the current SurfaceInfo BindInfo and realization too.
    std::uint64_t expectedVisual;
    std::uint64_t expectedSurface;
    std::uint64_t expectedBind;
};

struct alignas(8) Sample {
    std::int64_t sequence;
    std::int64_t qpc;
    HookPoint point;
    Decision decision;
    std::uint64_t visual;
    std::uint64_t surface;
    std::uint64_t bind;
    std::uint64_t realization;
    std::uint64_t hwnd;
    std::uint32_t ownerPid;
    std::uint32_t colorSpace;
    std::uint32_t pixelFormat;
    std::uint32_t originalEnable;
    std::int32_t nativeResult;
    std::uint32_t flags;
    std::uint64_t topLevelProperty;
    std::uint64_t windowNodeHwnd;
    std::uint32_t windowNodePid;
    Stage stage;
};

struct alignas(8) Telemetry {
    std::uint32_t size;
    std::uint32_t version;
    Lifecycle lifecycle;
    std::uint32_t capabilities;
    std::uint32_t hostPid;
    std::uint32_t lastWin32Error;
    std::int64_t candidateCalls;
    std::int64_t independentFlipCalls;
    std::int64_t targetMatches;
    std::int64_t verifiedSdrMatches;
    std::int64_t vetoedCandidates;
    std::int64_t demotedIndependentFlip;
    std::int64_t passedThrough;
    std::int64_t expiredLeaseCalls;
    std::int64_t readFaults;
    std::int64_t droppedSamples;
    std::int64_t activeCallbacks;
    std::int64_t lastCallbackQpc;
    std::int64_t acceptedRequestSequence;
    Sample latest;
    // Completed selected CANDIDATE samples only. IndependentFlip samples stay
    // in latest and cannot replace a candidate eligible for controller arming.
    Sample latestTarget;
};

struct alignas(8) SharedState {
    Request request;
    Telemetry telemetry;
};

struct alignas(8) StartConfig {
    std::uint32_t size;
    std::uint32_t version;
    std::uint64_t magic;
    std::uint64_t nonceLow;
    std::uint64_t nonceHigh;
    // Borrowed handles valid in the destination DWM process, previously
    // duplicated by the controller with QUERY_LIMITED_INFORMATION|SYNCHRONIZE.
    // Start duplicates them locally and retains its own copies. The controller
    // must close these two borrowed handles after Start returns in either case.
    std::uint64_t controllerProcessHandle;
    std::uint64_t targetProcessHandle;
    // UTF-16, NUL terminated. Only Local\\HDRCalibrate.SelectiveDwm.* accepted.
    char16_t mappingName[kMappingNameCharacters];
};

struct alignas(8) Capabilities {
    std::uint32_t size;
    std::uint32_t version;
    std::uint32_t bits;
    std::uint32_t maximumLeaseMilliseconds;
    std::uint32_t sharedStateSize;
    std::uint32_t startConfigSize;
    std::uint32_t requiresExactBinary;
    std::uint32_t remainsPinnedAfterStop;
};

static_assert(sizeof(Request) == 128);
static_assert(sizeof(Sample) == 112);
static_assert(sizeof(Telemetry) == 352);
static_assert(sizeof(SharedState) == 480);
static_assert(sizeof(StartConfig) == 304);
static_assert(sizeof(Capabilities) == 32);
static_assert(offsetof(SharedState, telemetry) == 128);
static_assert(std::is_trivially_copyable_v<SharedState>);
static_assert(std::is_standard_layout_v<SharedState>);

// Exported functions use DWORD WINAPI and an LPVOID argument so a controller can
// invoke the export from its verified module RVA. Start/Stop never run in
// DllMain. Stop makes the current session pass-through. A new Start may select
// a new target, with a new mapping and nonce (maximum 16 retained sessions).
// The DLL, original trampolines, mappings and process handles remain alive until
// host exit: calling FreeLibrary on it is not a supported cleanup operation.
// SelectiveDwmStart(StartConfig*) -> Win32 status, no implicit start on load.
// SelectiveDwmStop(nullptr) -> Win32 status, permanent pass-through.
// SelectiveDwmGetCapabilities(Capabilities*) -> Win32 status.

} // namespace selective_dwm
