#pragma once
#include "SelectiveDwmProtocol.h"
#include <limits>

namespace selective_dwm {

// Pure policy, shared by the native callback and an ordinary host-side test.
// Process liveness/creation, window ownership, and surface observations must be
// established separately; arithmetic here never treats expiry as recovery from
// a hung compositor or as proof that VRR/gamma/HDR are correct.
inline Decision ValidateLease(const Request& request, const Request& identity,
                              const std::int64_t nowQpc,
                              const std::int64_t nativeFrequency) noexcept {
    if (request.magic != kProtocolMagic || request.version != kProtocolVersion ||
        request.size != sizeof(Request) || request.reserved != 0 ||
        (request.mode != Mode::Observe && request.mode != Mode::CandidateVeto)) {
        return Decision::InvalidProtocol;
    }
    if (request.nonceLow != identity.nonceLow || request.nonceHigh != identity.nonceHigh ||
        request.controllerPid != identity.controllerPid || request.targetPid != identity.targetPid ||
        request.controllerCreationTime != identity.controllerCreationTime ||
        request.targetCreationTime != identity.targetCreationTime ||
        request.targetHwnd != identity.targetHwnd) {
        return Decision::IdentityChanged;
    }
    // QPC runs at a fixed positive frequency. Reject malformed arithmetic before
    // subtraction/multiplication and cap the *entire issued lease*, not merely
    // its remaining duration.
    if (nativeFrequency <= 0 || nativeFrequency > (std::numeric_limits<std::int64_t>::max)() / 5 ||
        request.qpcFrequency != nativeFrequency || request.leaseIssuedQpc < 0 || nowQpc < 0 ||
        request.leaseDeadlineQpc <= request.leaseIssuedQpc ||
        request.leaseIssuedQpc > nowQpc ||
        request.leaseDeadlineQpc - request.leaseIssuedQpc > nativeFrequency * 5) {
        return Decision::InvalidLease;
    }
    if (nowQpc >= request.leaseDeadlineQpc) {
        return Decision::ExpiredLease;
    }
    return Decision::Observe;
}

// Narrow SDR scope: only full-range P709 G22 (DXGI value 0) with ordinary 8-bit
// UNORM formats. FP16/scRGB, PQ, HLG, wide-gamut, limited-range and unknown
// metadata always pass through, including values that could otherwise be SDR.
inline bool IsVerifiedSdrFormat(const std::uint32_t colorSpace,
                                const std::uint32_t pixelFormat) noexcept {
    return colorSpace == 0 &&
        (pixelFormat == 28 || pixelFormat == 87 || pixelFormat == 88);
}

inline Decision EvaluateCandidate(const Request& request, const Sample& sample,
                                  const bool observedBeforeArm,
                                  const std::uint32_t capabilities) noexcept {
    if (!(sample.flags & OwnershipVerified)) return Decision::OwnershipUnknown;
    if (sample.hwnd != request.targetHwnd) return Decision::DifferentWindow;
    if (sample.ownerPid != request.targetPid) return Decision::DifferentProcess;
    if (!(sample.flags & HasPixelFormat) ||
        !IsVerifiedSdrFormat(sample.colorSpace, sample.pixelFormat)) return Decision::NotVerifiedSdr;
    if (request.mode == Mode::Observe) return Decision::Observe;
    if (!observedBeforeArm) return Decision::MissingObservation;
    if (!(sample.flags & HasBindInfo) || !sample.bind || !request.expectedBind) return Decision::SurfaceChanged;
    if (!request.expectedVisual || !request.expectedSurface ||
        request.expectedVisual != sample.visual || request.expectedSurface != sample.surface ||
        request.expectedBind != sample.bind) return Decision::SurfaceChanged;
    const auto needed = sample.point == HookPoint::Candidate ? VetoCandidates : DemoteIndependentFlip;
    if (!(capabilities & needed)) return Decision::CapabilityUnavailable;
    return Decision::Veto;
}

} // namespace selective_dwm
