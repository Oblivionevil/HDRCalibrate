# Native 26100.9278 candidate — 2026-09-25

The first sections record the initial isolated build and offline compatibility
result; the later hardening section supersedes its source/test status. This is
**not a released or live-accepted HotS gamma backend**. The installed application
remains 1.7.2 and does not enable per-program forcing on 9278. In the initial
build, no managed allowlist, startup approval, native policy, SDR filter, hook
callback, or lease behavior was changed; later reader hardening is detailed below.

## Build selection and identity

- Default and explicit `26100.8875` select the existing complete 8875 contract.
- Explicit `26100.9278` selects a separate header containing all 15 RVAs, all
  12 byte guards, and the exact 9278 file identity. Unknown selectors fail.
- Preflight and the hook consume the same selected contract. This is not a
  runtime hash-only override or an automatic Windows-update migration.
- Candidate builders reject missing, reused, reparse-point and non-candidate
  output paths. Object files are isolated with the candidate output.
- System dwmcore SHA-256:
  `40579ECFA394395677FEC84014C32BA5495F334B6D96C5653FD5CAA129DC1222`.
- Independently inspected: 15 symbol mappings, 12 guarded spans, 8 vtable slots,
  5 relative targets, private field arithmetic and both x64 signatures.
  The independent review also inspected the previously omitted Flip cold chunk
  and the complete sparse-property helper.

## Actual results

| Check | Result | Boundary |
| --- | --- | --- |
| Compiled-header/file contracts and build-output rejection | 85 passed | Disk file and test-owned memory only |
| Hook host tests | 72 passed | Synthetic host, not DWM |
| Controller host tests | 82 passed | Synthetic/local metadata tests |
| Actual candidate controller CLI/export/negative requests | 70 passed | No live DWM access |
| Actual candidate controller/.NET pipe tests | 3 passed | STOP before GO and invalid self-shell targets |

The CLI and pipe tests used hash-identical copies of the actual candidate
controller and hook, in a fresh `verification-bin` directory:

- Controller SHA-256:
  `6BB26448B3121CB7A200E5E56121DF60EB9C0E68813C6C8DD3988723361CAF4C`.
- Hook SHA-256:
  `6E8732C8AD0B8FF35EDA1ECFFDF842102E1B4B829ED3E2DC207A4469735D79D0`.

Evidence root: `artifacts/native-9278-candidate-20260925/`. Relevant files are
`file-tests.log`, `hook-build-host-tests.log`, `controller-build-host-tests.log`,
`cli-tests-verified/summary.json` and `pipe-tests/summary.json`.
Independent disassembly evidence: `artifacts/native-9278-review-20260925/`.

The new `Test-BuildContract.ps1` compiles `ContractFileProbe.cpp` for the default,
both explicit known builds, and one rejected unknown build. It then compares
the compiled constants to the installed file and the retained independent
`artifacts/dwm-9278-native-analysis/contract-map.json` and
`byte-comparison.json`. Those local analysis inputs are required; this lab test
is not yet self-contained on a fresh clone. Mutations change only a byte array,
never the Windows file. This is a separate disk-file model of the checks, not
execution coverage of the hook's real in-process `VerifyContract` function.

## Remaining acceptance gates

- Direct synthetic coverage of `ReadCurrentSurface` and `ReadPlane`, including
  wrong interfaces, realization adjustment, bind changes, HDR/source changes
  and read faults; real `ReadVisualIdentity` and contract-path coverage remain
  distinct from synthetic dispatch tests.
- Additional mapped-image checks for the realization outer getter and plane
  constructor/layout, plus image size. The on-disk hash does not prove that
  every instruction in the loaded module is unchanged.
- Native prologue/trampoline compatibility in the intended runtime, followed
  by a separately controlled observer/Stop/expiry acceptance. No candidate hook
  was loaded into DWM during this verification.
- Visible HotS gamma effect, native HDR isolation, VRR preservation and latency.
  Existing PresentMon traces do not establish these properties.

Do not replace the historical live-runner hash pins, enable the managed 9278
allowlist, or ship this candidate as accepted merely because offline tests pass.
Independent-flip demotion remains disabled. A lease cannot recover a compositor
thread that is already blocked.

## HotS menu measurement, separate from candidate tests

The rendered menu is a valid test scene; no login is required for this check.
HotS PID 19200 remained in the foreground for all 250 samples of a 25-second
PresentMon 2.6.0 run. It recorded 1,560 `Composed: Flip` presents, but all display
latency/duration values were `NA`; reported ETW loss and overflow were zero.
This reproduces the display-correlation limitation of the earlier versions,
not proof that the visible game drops every frame. No gamma was changed for
this trace. The globally saved gamma is 2.2; selective forcing is not active.

App, DWM and HotS retained their process identities and remained responsive.
All eight checked HDRCalibrate state files retained their hashes. The frozen v3
hook still has SHA-256
`128556F6904418F928D72875B974674FC0A0BF1C90209570D7E6D0D1AB6B0596`.
No game files, driver settings, HDR mode, existing ETW sessions or installed
binaries were changed. Full menu evidence: `artifacts/hots-live-20260925/`.

## Subsequent hardening and reproduced reader defect

The production mapped-image verifier is now a separate bounded function called
by `VerifyContract` after its unchanged exact system-path and file-SHA checks.
The function checks x64 PE headers, sizes and guarded bytes without publishing
global pointers on failure; inaccessible memory is rejected through SEH.
`K32GetModuleInformation` supplies the reported module size, but that value is
PE-derived, not an independent memory-layout proof. The 9278 contract separately
pins `SizeOfImage` to `0x443000`.

For 9278, two additional complete instruction spans are now checked:

- Realization outer getter: 83 bytes at RVA `0xC0C40`.
- Plane constructor: 535 bytes at RVA `0x264718`.

Independent rereading of the installed DLL confirmed both arrays. None of its
11,721 DIR64 relocations intersects either span. The old 12 base guards remain;
the candidate has 14 guarded spans totaling 872 bytes. This is still not a
claim of whole-loaded-module identity or a replacement for live acceptance.

New direct reader tests found a real missing-bind defect: `ReadPlane` could
substitute the backing surface's bind when the plane's own bind was zero. The
unmodified test first produced `CompleteSDR=true` for this invalid case. The
minimal production fix rejects a missing plane bind before surface access.
The same test then returned false, bind 0, flags 0 and `CompleteSDR=false`.
Independent-flip demotion remains disabled; this fixes metadata validation,
not a newly enabled flip manipulation.

Latest evidence: `artifacts/native-9278-candidate-20260925-hardening/`.

| Actual check | Result |
| --- | --- |
| Before fix, same direct-reader test | 168 passed, exactly 1 expected failure |
| Fixed 9278 hook host suite | 169 passed, 0 failed |
| Fixed default-8875 hook host suite | 165 passed, 0 failed |
| Current controller host suite | 82 passed, 0 failed |
| Current controller CLI/negative tests | 70 passed, 0 failed |
| Current controller/.NET pipe tests | 3 passed, 0 failed |

The hook suites include 51 direct reader checks, every byte mutation of each
guarded span through the actual production verifier, PE/bounds/read-fault
negatives, and a read-only `SEC_IMAGE_NO_EXECUTE` view of the installed Windows
file. They also prepare MinHook trampolines from owned copies of both native
prologues without enabling or executing those copies. Existing synthetic
13-argument/return/LastError and lease/Stop tests remain in the suites.
The CVisual HWND case explicitly uses a local test stub: real sparse-property
execution and the concrete live objects are not claimed as covered.

Latest frozen verification bundle: `verification-bin/` in that evidence root.

- Controller SHA-256:
  `9E616F0949707DE0BF394FB1AE18575C4DC6BEFBF86A636C58A2D71DC8A10A3C`.
- Hook SHA-256:
  `45F9C9B0F3776350C953C20587447708EFA03725EFB6BE9DC16EB78E8C824B63`.

The controller adds `sample_stage` and `expired_lease_calls` to its JSON
telemetry, using existing protocol fields; wire layout and policy are unchanged.
The first controller-host invocation used a relative DLL path and failed its
exact-path assertion (81/1). An absolute-path rerun passed 82/82; the build-test
wrapper now canonicalizes its HookPath so relative caller inputs are valid.
This harness issue is separate from the reproduced plane-bind defect.

Remaining before release: the separately authorized first observer run, actual
target metadata reachability, real DWM callback completion/Stop/expiry and the
visible SDR/HDR/VRR/latency acceptance. The initial candidate and all historical
payloads remain preserved. No new native code has been activated in DWM.

## Bounded observer runner: offline verification only

`Run-9278Observer.ps1` defaults to `ValidateOnly`. It holds read locks on the
hash-pinned candidate binaries and runs the controller with `--validate-only`;
this path neither loads the hook nor accesses DWM or HotS. An explicit
`-RunLive` is required for the separate five-second observer. No live run has
been authorized or performed as part of the results below.

`Tests/Test-9278Observer.ps1` passed all 134 cases on both PowerShell 7 and
Windows PowerShell 5.1. These tests extract only allowlisted pure/local-file
functions; they do not invoke the controller, initialize native process APIs,
or access DWM or the game. They exercise evidence rejection, exact identity
binding, path/quoting rules, real test-owned file locks and the opt-in gate.
Reports: `artifacts/native-9278-observer-tests-20260925/powershell7.json` and
`powershell51.json`.

The actual frozen controller also passed the runner's `ValidateOnly` invocation:
exit code 0, `process_access=false`, `dll_loaded=false`, `LiveStarted=false`.
`Accepted=false` is intentional because argument validation is not live
acceptance. Evidence: `observer-validation-result.json` in the hardening root.

Live acceptance requires three fresh, distinct and complete SDR candidate
samples bound to the selected HotS window/process; a callback after the lease
deadline; successful Stop; `StoppedPinned` recovery with no active callbacks;
and unchanged settings/process identities with no relevant crash evidence.
Exit code 0, stale samples, missing samples or fabricated success fields alone
cannot pass. Gamma, native HDR isolation, VRR and latency remain untested by
this observer even if it subsequently passes.

The observer ends after its five-second lease; loaded code stays pinned in DWM
until DWM exits. A timeout can stop only the retained, runner-owned controller
process, never DWM or HotS. It does not guarantee recovery from a blocked
compositor thread. The native live identity-verification branch and actual
callback behavior still require separately authorized runtime verification.

## First authorized live observer, 2026-09-25 16:51 UTC

The separately authorized five-second observer has now run once with the
frozen controller/hook hashes above. This supersedes the earlier no-live-run
status; it does not enable the installed application's 9278 allowlist.

- Native Start succeeded; 1,385 candidate and 1,403 independent-flip callbacks
  completed. No read faults, vetoes, demotions or game writes were reported.
- Natural lease expiry was exercised (182 post-deadline callbacks). Stop
  succeeded and recovery reached `StoppedPinned` with zero active callbacks.
- DWM, HotS and HDRCalibrate retained their process generations and remained
  responsive. All eight settings-file hashes matched; no relevant crash events
  were returned by the post-run check.
- **Target acceptance failed:** zero HotS ownership matches and zero selected
  SDR samples. Controller exit 0 therefore did not pass the runner's acceptance.
  The sampled latest records were unrelated/unowned point-2 visuals; their
  PQ/10-bit metadata must not be attributed to HotS.

Evidence: `observer-live/observer-20260925-165114-e12b40607dea4a70973d9a384a168c0a/`
under the hardening evidence root (`summary.json`, controller stdout/stderr).

The later screenshot inspection initially showed Brave covering the selected
game window. Bringing the existing HotS window to the foreground displayed an
intact rendered menu. This makes background/occlusion a plausible explanation
for the absent target samples, but does not establish which window was in front
throughout the earlier observer interval. The menu itself is a valid test scene.

No gamma was changed. The observer is stopped; its DLL and detours remain pinned
in DWM and forward to the native functions without an active session. Neither
the game nor DWM was restarted. Any follow-up must use the exact same pinned
binary and retained DWM generation; the first-observer runner intentionally
continues to reject an already loaded hook.

## Foreground observer and bounded candidate veto

The separate `Run-9278StoppedObserver.ps1` reuses only the exact already-loaded
candidate after validating the previous real JSONL and clean Stop. It passed
44 offline checks on both PowerShell 7 and 5.1. Its default `ValidateOnly` mode
was also exercised against the actual completed first run. No native binary or
first-observer runner was changed for reuse.

At 17:01 UTC the foreground follow-up passed the full observer gate: 19 fresh
selected SDR snapshots and 3,020 target/SDR matches. The selected identity was
HotS PID 19200 / HWND 28051626, format 87, color space 0, bind 4. All 80 separate
foreground samples inside its lease identified HotS. Natural expiry (210
callbacks) and `StoppedPinned` recovery passed with no read faults, vetoes,
demotions, crash events, changed settings or process generations.

`Run-9278CandidateVeto.ps1` requires that accepted observer and the same pinned
module/identities. It defaults to validation; the live mode performs one
`--veto --duration-ms 5000` request. The controller first observes the source
again, then arms only a fresh complete target-SDR visual/surface/bind tuple.
The five-second modifying lease begins at `veto_armed`; observation and recovery
add time outside that lease. No continuous mode or independent-flip demotion is
enabled. Its real validation path and rejection of the zero-target first-run
report were tested before live use, followed by source review.

| Live run | Native result | Foreground evidence |
| --- | --- | --- |
| 17:06 UTC bounded veto | 1,200 target candidates vetoed; expiry and Stop passed | HotS 0/80 samples: not a foreground display test |
| 17:08 UTC foreground veto | 1,201 target candidates vetoed; exactly 5,000 ms modifying lease | HotS 80/80 samples during the lease |

The foreground veto produced 19 sampled completed candidate vetoes, all bound
to the selected HotS window/PID with source color space 0 and format 87. There
were zero incorrect-target/non-SDR sampled vetoes, zero read faults, zero
demotions and zero active callbacks after Stop. There were 206 expired-lease
callbacks, no post-expiry veto increase, no relevant crash events and unchanged
settings. A screenshot before and after this run showed an intact rendered
HotS menu. Screenshots were outside the measured interval, not calibrated
color measurements or continuous video of the test.

Evidence in the hardening root:

- Observer: `observer-followup-live/stopped-observer-20260925-170116-8f922549d4624634bf71b49421be9ae4/`.
- Background veto: `candidate-veto-live/veto-20260925-170650-a1f48494c736435d94ad486c29c80498/`.
- Foreground veto: `candidate-veto-visible-live/veto-20260925-170854-b0365b5361ba4b159d79f3a7a4c278af/`.
- Independent read-only foreground traces: `foreground-followup.json`,
  `foreground-veto.json`, `foreground-veto-visible.json`.

**Accepted scope is native selected-SDR candidate rejection and bounded
expiry/Stop, not visible gamma, complete HDR isolation, preserved VRR or input
latency.** Gamma settings were not changed. The installed 1.7.2 application,
managed 9278 deny gate, startup approvals and historical binaries remain
unchanged. All four new sessions are stopped; the one native DLL remains pinned
and forwards without an active session until DWM exits normally. No DWM/game
restart, driver change, registry change, game hook, installer update or release
was performed for these tests.

API references: [GetModuleInformation](https://learn.microsoft.com/en-us/windows/win32/api/psapi/nf-psapi-getmoduleinformation),
[MODULEINFO](https://learn.microsoft.com/en-us/windows/win32/api/psapi/ns-psapi-moduleinfo),
[non-executing image mapping](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createfilemappinga).

## Screenshot comparison follow-up, 17:21-17:22 UTC

Two additional bounded sessions completed with 1,200 selected candidate vetoes
each, natural expiry, retained process identities and successful Stop. Evidence:
`candidate-veto-capture-live/veto-20260925-172147-9b3f69ca52ee49ebba2b31c30fd1638e/`
and `candidate-veto-capture-live/veto-20260925-172210-a10f4d1a32654198bbadd6be8a6d067c/`.
The first capture was too late (after the runner returned); it is not an active
test capture. The second runner and window capture were dispatched concurrently.
Window capture ran at 17:22:12.565-17:22:12.699 UTC, with the after capture at
17:22:29.611 UTC and initial baseline at 17:21:27.555 UTC. Screenshots were
inspected in the tool output, not persisted as additional account-containing
image files. No login interaction or game setting changes were performed.

The rendered scene remained intact. No unambiguous gamma difference was visible
in these captures. This is an inconclusive gamma result, not evidence that the
physical display is unchanged: window capture is not a calibrated measurement
of final HDR output, and no target-gamma setting was changed. The native veto
counter must not be promoted to gamma acceptance. VRR and latency remain
unverified; neither a release allowlist expansion nor a persistent activation
is justified by this comparison. All six sessions are now stopped. The next
useful test must distinguish actual corrected shader execution/output from
candidate rejection; repeating the same veto counter test adds no gamma proof.
