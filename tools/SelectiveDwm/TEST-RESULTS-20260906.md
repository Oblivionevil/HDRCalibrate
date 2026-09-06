# Test record — 2026-09-06

## Outcome

**Partial implementation, not accepted as a working HotS gamma/VRR feature.**
After the explicitly authorized DWM restart, v3 recognizes selected SDR source
metadata and performs bounded target candidate vetoes. Five-second expiry and
Stop pass through cleanly, and PQ/scRGB negative controls refuse arming. The
full feature is **not accepted**: even before v3 was loaded, the new DWM baseline
reported Composed: Flip and no enabled/requested VRR. Later baseline/recovery
traces contain unreliable display tracking. No gamma correction, latency gain
or preserved VRR during selective correction has been demonstrated.

No game process was terminated or modified. No game files, display settings,
global MPO controls, color profiles, startup entries or installed application
binaries were changed for the selective prototype. No GitHub release/push was
performed. Test executables use separate output directories.

## System and retained identities

- Windows DWM core: `10.0.26100.8875`, SHA-256
  `825726F26BD6B5A02B49F6145963B54D537D3D1239055DDA28D1FB0B3E864EF3`.
- Historical v2 observer: DWM PID 2092, creation FILETIME
  `134331235041082126`. Following the user's explicit `ja`, DWM was restarted
  once at `2026-09-06T11:22:17.8Z`. Current DWM PID 31032, creation FILETIME
  `134331673378020485`, retained throughout the subsequent v3 tests.
- HotS PID 29940, start `2026-09-06T03:58:53.7207417+02:00`; same running process,
  HWND 196842. Only its identity/liveness was queried outside DWM.
- RTX 5090, driver 616.56; PG32UCDP, DISPLAY1 at 3840x2160. Output reports PQ /
  BT.2020, 10 bits. Windows SDR white is 240 nit. DXGI peak metadata is
  507.636 nit; this is metadata, not an optical measurement.
- DWM preflight allowed ordinary process access with no protected-process or
  signature/dynamic-code restriction. CFG flag 1. CET flag 256 is
  `CetDynamicApisOutOfProcOnly`, not proof of enabled user shadow stack.

## Automated evidence

| Suite | Verified result | Scope |
| --- | --- | --- |
| WPF/.NET 4.8.1 regression | 58 passed; build 0 warnings, 0 errors | Existing gamma math, shader manifests, timed protocol, Windows-update/startup guards, profiles, UI/viewmodel and installer contracts |
| Controller | 17 passed | Invalid/duplicate arguments, bounded lease arguments and local-only DLL export inspection; no live DWM access |
| Native v2 host | 60 passed | Real synthetic MinHook detours, 13-argument ABI, result/LastError, source exclusions, identity, lease, Stop/race and retained-session contracts |
| Corrected native v3 host | 72 passed; build 0 warnings, 0 errors | Adds real production-reader tests for both concrete metadata layouts, mixed/unknown getter rejection, HDR exclusions and candidate-proof retention |
| Color fixture | 11/11 cases; 118 math/parser checks | Source creation and bounded SDR/scRGB/PQ swapchain smoke tests |
| Fixture GPU readback | 18,432 RGBA checks on WARP and 18,432 on RTX 5090 | Own offscreen texture values, not monitor output |
| PresentMon analyzer | Self-test passed | Numeric filtering, percentiles, phase boundaries, drops and empty phases |

Detailed fixture results:
`Fixture/test-results/20260906-124130-272/summary.json` (generated/ignored).
Controller results:
`artifacts/selective-dwm-20260906/controller-tests-1137643181354b13b112fe7b9057b34f/summary.json`.
The corrected v3 package also passed all 17 controller tests:
`artifacts/selective-dwm-20260906/controller-tests-9d91c3d98b324e90a5b5c2c974604655/summary.json`.
Root independently reran all 72 v3 host checks successfully. The v3 DLL is
`artifacts/selective-dwm-20260906/v3/bin/HDRCalibrate.SelectiveDwmHook.dll`, SHA-256
`128556F6904418F928D72875B974674FC0A0BF1C90209570D7E6D0D1AB6B0596`.
It first passed the ordinary canary host and was subsequently loaded into the
separately authorized new DWM session (see the v3 live record below).
See also [the native implementation record](Hook/README.md).

## Historical v2 observer and trace evidence (before the authorized restart)

All entries below are **observation only**, with zero vetoes, zero read faults,
successful Stop, unchanged DWM/target identities and no post-expiry veto.

| Target | Hook evidence | Stable PresentMon frames, baseline / observe / recovery | VRR evidence |
| --- | --- | --- | --- |
| Initially occluded own window | 2,520 candidates; 2,546 independent-flip calls; 0 target matches | No presentation acceptance inferred | Not an accepted foreground test |
| Fullscreen own SDR fixture, 120 FPS | 644 candidates; 644 independent-flip calls; 1,202 target matches; 0 verified-SDR matches in v2 | 570 / 540 / 570, all Hardware Composed: Independent Flip, zero drops | 47 / 45 / 48 sampled target-foreground readings, flags 0x17 |
| Foreground HotS menu | 1,203 candidates; 1,203 independent-flip calls; 2,246 target matches; 0 verified-SDR matches in v2 | 1,062 / 1,011 / 1,068, all Hardware Composed: Independent Flip, zero drops | 47 / 45 / 47 sampled target-foreground readings, flags 0x17 |

Raw reports under `artifacts/selective-dwm-20260906/`:

- `live-fixture-observe-v2/controller.jsonl`
- `experiment-20260906-130503-observe-1ed232d5/summary.json`
- `experiment-20260906-130750-observe-bb44d470/summary.json`

Each experiment directory contains the PresentMon CSV, controller JSONL and
NVAPI/VBlank CSV. Its trace uses the controller's actual QPC lease, with 250 ms
transition margins. These readings show that the **observer** did not disable
VRR; they do not establish VRR during forced composition. VBlank rates were not
accepted as a physical-panel measurement. Present-to-display statistics are
preserved in the reports; no latency gain or zero-overhead claim is made.

The Computer Use skill was used only for necessary foreground selection and
window screenshots. The visible own SDR patch grid and HotS menu were complete
and not fragmented. Captures were outside the trace measurement. These images
are UI/surface checks, not calibrated HDR screenshots or optical gamma proof.

## Confirmed v2 defect and offline correction

`Decision 13 / NotVerifiedSdr` was caused by a missing allowlisted concrete
pixel-format implementation, not by guessing from image brightness:

- Selected HotS surface has the expected GCSI vtable `0x2F8298`, current BindID 4
  and realization source getter `0x2D6DC0`.
- Its nested pixel-format interface is **CD2DBitmap**, vtable `0x2F97E8`, getter
  `0x1BF4A0`, rather than the originally allowed CD3DSurface interface.
- The exact 24-byte getter performs three direct reads: format at interface
  `+0x50`, alpha mode `+0x74`, color space `+0x90`. PDB, disassembly and runtime
  pointer/code checks agree.
- Bounded read-only verification returned format 87 (B8G8R8A8_UNORM), alpha 3,
  color space 0. The final check used 18 reads / 180 bytes, rechecked the live
  surface/binding identity and performed **zero remote calls/writes**.
  Evidence: `artifacts/selective-dwm-20260906/hots-source-leaf-verified.json`.

The source correction adds only that exact class/getter pair with its own
offsets; unknown classes and HDR formats remain rejected. It does not weaken
the source gate or treat every 8/10-bit surface as SDR. Additional production
reader tests cover the distinct layouts and HDR exclusions.

A second controller-arming defect was found by reviewing the real callback
sequence: the immediately following independent-flip sample could overwrite
the eligible candidate before the controller's next poll. V3 retains completed
selected candidates in the existing `latestTarget` slot; general telemetry
still includes both hook points. A production-path host regression confirms
that the independent-flip callback cannot overwrite this arming proof. The
protocol version and wire layout remain unchanged.

## Historical v2 live-state boundary and authorized resolution

The old v1 DLL was loaded but its Start failed 1307 **before hook installation**.
It was inert and was not unsafely unloaded. V2 installed two hooks and was
StoppedPinned/pass-through until the authorized restart. Its SHA-256 remains:
`767DDA29AB31C0C4F41C571EB2FEC82668FD4EE43CD0B334F6BA93B114538E44`.

Do not overwrite either loaded DLL, patch an internal undocumented unload
entry, or load competing detours. V2 has no supported detach/upgrade export;
the needed unreferenced MinHook disable path is not established in the linked
image. A zero active-session callback counter also does not prove that no
passive trampoline invocation is running. Keeping the old code allocated
avoids a use-after-free but does not make arbitrary live replacement safe.

The user explicitly authorized the fresh DWM session. One restart was performed;
the new process was verified to contain no previous HDRCalibrate hook image
before loading v3. HotS and the installed application retained their identities.
Restart evidence: `authorized-restart-before.json` and
`authorized-restart-result.json` under `artifacts/selective-dwm-20260906/`.
No automatic activation approval has been created for this prototype.

## V3 live record after the authorized restart

Every completed v3 session has zero read faults, successful Stop status 0,
retained DWM/target identities and no post-expiry veto. A retained historical
`latestTarget` veto sample does not indicate an active request after Stop:
current lifecycle and cumulative veto count are checked separately.

| Test | Source and decision | Presentation / VRR result |
| --- | --- | --- |
| Own fullscreen SDR observer, 120 FPS | Format 28, color space 0, BindID 1; 3,556 target/SDR matches; zero vetoes | 569 / 540 / 570 displayed frames, Composed: Flip throughout, flags 0x12 throughout |
| Own fullscreen SDR candidate veto, 120 FPS | 1,184 vetoes during the five-second lease | Baseline 1/569 displayed; active 540/540; recovery 0/516; flags 0x12 throughout |
| Own PQ negative control | Format 24, color space 12; 3,284 target matches, zero SDR matches; arm refused, expected exit 20 | Zero vetoes, source was not relabeled SDR |
| Own scRGB negative control | Format 10, color space 1; 3,353 target matches, zero SDR matches; arm refused, expected exit 20 | Zero vetoes, source was not relabeled SDR |
| Foreground HotS observer | Format 87, color space 0, BindID 4; 2,400 target/SDR matches; zero vetoes | 567 / 540 / 570 trace records all marked Dropped, Composed: Flip; flags 0x12 throughout |
| Fresh moving SDR fixture, 120 FPS | 1,200 vetoes; 3,960 own Present calls over 33 s; zero occlusion/timeouts | Baseline 0/570 displayed; active 540/540; recovery 0/570; starts ETW before process creation, no ETW stderr warning |
| Fresh moving SDR fixture, requested 137 FPS | 1,200 vetoes; 4,047 own Present calls over 33 s; zero occlusion/timeouts, 473 missed scheduling slots | Baseline 0/571 displayed at about 120 CPU Presents/s; active 615/615 at about 137; recovery 0/570 at about 120; not a stable 137 FPS baseline |

Evidence directories under `artifacts/selective-dwm-20260906/`:

- `experiment-20260906-132254-observe-54bc6a7e`
- `experiment-20260906-132510-veto-fa606479`
- `live-pq-negative-v3`
- `live-scrgb-negative-v3`
- `experiment-20260906-133049-observe-39034477`
- `moving-20260906-134017-120-veto-e6f8f19b`
- `moving-20260906-134155-137-veto-15d52406`

The first v3 observer trace starts before its initial DLL load: the baseline
already reports Composed: Flip and flags **0x12**. Thus the status change cannot
be attributed to a selective veto; no veto had yet occurred. The restart is a
temporal boundary, not a confirmed driver/root cause. With the documented
getter layout, 0x12 has Possible and DisplayInVRRMode set but Enabled and
Requested clear; DisplayInVRRMode alone is insufficient evidence of active VRR.
The relevant display is NVAPI `0x80061082`, PG32UCDP / DISPLAY1.

The first veto trace's 708 ms CSV gap is **not** evidence of an application
stall: its fixture JSONL continues normal successful Presents, with 84
submissions absent from the trace. The moving fixture removes a constant-image
confound, starts ETW before its first swapchain/Present, and requests
`--track_etw_status`; the baseline/recovery Dropped discrepancy persists.
Both moving tests retain 280 target-foreground VRR samples, all flags 0x12 on
the expected display. Their `[ETW Status]` records are in PresentMon stdout,
not the blank appended CSV columns: counters report zero lost events, lost
buffers and overflowed Presents. These counters do not prove complete
display-event correlation. The requested 137 FPS run also changes CPU cadence
between phases, so it is not a matched-rate latency comparison.
An independent comparison to the fixture's own QPC log corroborates that
120.02 -> 136.78 -> 120.02 submissions/s change. Mean latency-object wait is
about 7.74 -> 0.03 -> 7.75 ms; it is a reversible swapchain-backpressure effect,
not merely a CSV omission or proof of adaptive scanout. The moving tests'
phase frame counts match the fixture's own counts exactly. Their driver VBlank
counters remain about 240 Hz in every phase (239.88..240.17 Hz over the accepted
4.4..4.7-second intervals), despite the requested 120/137 FPS. Both the getter
and VBlank QPC endpoints are inside the phase and the expected display/target
foreground sample is required. Driver counters are not optical panel readings.

PresentMon 2.5.1 drops `IsLost` / failed records and documents incomplete
correlation when joining existing workloads. These are diagnostic limitations,
not a confirmed explanation for this particular trace. See the official
[output handling](https://github.com/GameTechDev/PresentMon/blob/v2.5.1/PresentMon/OutputThread.cpp#L429-L433)
and [correlation code](https://github.com/GameTechDev/PresentMon/blob/v2.5.1/PresentData/PresentMonTraceConsumer.cpp#L2084-L2097).

The moving fixture was built separately with `/W4 /WX`: 8/8 offline test cases,
499 CPU checks and 55,296 RGBA readback checks each on WARP and RTX 5090 passed.
Its SHA-256 is `238DA49635DAE0CDEC584B2C69C4AA84E9C189AEFBCD77358C8A7E903309DB95`.
These verify generated source textures, not physical output colors.
After the live runs, all 72 native host checks, 17 controller checks, eight
moving-fixture offline cases and the WPF regression executable were
independently rerun successfully. The loaded
v3 DLL and both running user applications were not rebuilt or replaced.
The moving-runner result gate was tightened to reject helper failures and
target/DWM identity loss; ten isolated gate regression cases passed without
live activation. The two measured runs already satisfy those exit/identity
checks. `logman query -ets` subsequently found no owned experiment session.

### Remaining acceptance / current state

No active veto or gamma correction remains. V3 code and trampolines remain
pinned/pass-through in DWM, as designed; this is not a DLL-unload claim. No HotS
veto was attempted while its baseline VRR/display evidence was unsuitable.
Screenshots showed an intact SDR patch grid, PQ grid and HotS menu, but do not
prove physical HDR preservation or a gamma curve. PQ/scRGB exclusion tests did
not run simultaneously with an active SDR gamma correction.
The final foreground HotS screenshot at approximately 13:51 local also shows
an intact menu. The following read-only driver check still returns 30/30
target-foreground samples with flags 0x12. No relevant display-driver event or
DWM/HotS/application crash event was found in the queried System/Application
logs since the restart. Final process/session evidence is saved in
`artifacts/selective-dwm-20260906/final-state-after-v3-tests.json`.

Before integrating gamma into the program, establish a trustworthy foreground
baseline, demonstrate original-path restoration, then validate actual gamma,
HDR/non-target isolation and VRR/latency under correction. The existing gamma
engine apply/reset path remains unused here because it restarts DWM. Any further
DWM restart, HDR toggle or global driver/display change requires coordination;
the completed restart authorization is not reused for a different system change.

## Follow-up: exact process binding and read-only driver policy

The separately built controller now accepts a paired expected DWM PID and
creation FILETIME. It retains a metadata/liveness handle before preflight and
checks the generation again on the remote-operation handle. An independent
source review found no blocking defect in this transition. A deterministic
simulated restart between those two checks has **not** been tested.

The final controller SHA-256 is
`5949ACC1C2250ACBE40159A68B77A1C668F7C501FDCAF6CB2ACCECAB54135D2F`.
All **32** parser, dry-run and local DLL-inspection cases passed; none activates
DWM. Evidence: `controller-tests-b5a2111f7cd14091bf8247b71068f14b/summary.json`.
Two additional negative controls on that exact final binary rejected a wrong
PID and a deliberately wrong creation FILETIME with exit 21, before preflight,
remote writes or remote calls. A nonexistent target PID supplies a further
non-activation boundary. Evidence:
`controller-generation-bound/generation-guard-tests-final.json`.
The frozen v3 hook and historical live-run controllers were not replaced.

This binding is preparation, **not gamma integration**. The existing gamma
engine restarts DWM on apply/reset. A combined supervisor must first verify the
successful gamma response, convert its UTC DateTime ticks with
`ToFileTimeUtc()`, and bind selection to that exact returned generation. Its
selection controller must outlive the gamma worker, which the current timed
test watchdog may terminate after commit. Selection must expire/stop before
gamma rollback, without automatic reattachment to a replacement DWM process.
No combined gamma/selection test or additional restart was performed here.

### DRS sanity check

`Diagnostics/ReadNvidiaVrrPolicy.cpp` uses only public, read-only NVAPI getters
and an owned temporary DRS session. Its SDK revision and query IDs are pinned;
missing values stay unknown rather than becoming compiled-in defaults. No
driver profile, registry value, display mode or game was changed. The extended
build has SHA-256
`AF2330BEBADBB913775B06C2099A60FD450D5C0D003AFE2F479B3180DCEF5E38`.
Its ten offline cases (28 internal synthetic checks) passed. SDK identity and
build/test results are under `vrr-policy-sanity/`; the earlier six-case build
and its first read-only logs remain separately under `vrr-policy/`.

The actual full HotS path resolves to the predefined **Heroes of the Storm**
profile. The owned fixture has no application profile (status -166). Base and
current global lookups both report **Base Profile**. Results from the extended
read-only query:

| Query | Observed result | Interpretation |
| --- | --- | --- |
| Names of all five public VRR setting IDs | Status 0, names returned | The driver recognizes these IDs |
| Values of those five IDs in base/global/HotS profiles | Status -160, `NVAPI_SETTING_NOT_FOUND` | Current policy values are unavailable; do not infer fullscreen-only, windowed support, or disabled VRR |
| Standard `VSYNCMODE` control, ID `0x00A879CF` | Status 0, value `1199655232` / `force_on` | A real DWORD setting is readable; HotS inherits it from the global profile |

This excludes a blanket failure of the DRS `GetSetting` call, but does not
establish why the VRR values are absent or prove the cause of the earlier
presentation change. Global forced V-Sync alone is not evidence that VRR is
broken. Both queries exited 0 with successful session destruction and NVAPI
unload. Raw evidence: `vrr-policy-sanity/live-readonly/{hots,fixture}.jsonl`.

A final three-second status query returned 30 successful 0x12 samples on
display `0x80061082`, but foreground PID was **1960, not HotS**. It is not an
additional foreground HotS acceptance test. Its CSV/stderr are in that same
`live-readonly` directory. The foreground HotS evidence above remains the
relevant measured baseline. Gamma/VRR/latency acceptance is still outstanding.

## Follow-up: one authorized Windows-HDR OFF/ON cycle

The user separately approved one short Windows-HDR cycle. It ran at
approximately **15:11:22–15:11:25 local**, without restarting HotS, DWM or the
installed application. This is a completed diagnostic, **not a VRR/gamma fix**.

The standalone helper was built with SDK 26100, `/W4 /WX /O2 /MT /guard:cf /sdl`.
Source SHA-256:
`CE2B2C4C37FF97EC6451A612234BFCAA57BFC0858F547E90814DC2CAD87127AB`.
Binary SHA-256:
`58D2D86960A7C7D9E33B6706F8CEDA24CEA11BAB258F08443335E6C34F6229A0`.
It was not rebuilt between the successful safety simulations and the live run.

The first synthetic launch correctly refused with
`guardian_job_isolation_refused`, **before any live display call**. A local WMI
process start with explicit breakaway/no-window flags allowed the guardian to
prove it was outside all jobs without changing existing job limits. Normal,
crash-after-OFF and late-OFF simulations then passed, followed by a read-only
check of the actual console display in that same launch context. The complete
unchanged offline suite subsequently passed **7/7 cases and 23 pure checks**,
including expected exit codes 25 for the simulated crash and 124 for the
watchdog timeout. The late-OFF case executed zero OFF calls. All synthetic
setter records have `simulation=true`; no display APIs ran in those tests.

The real log contains exactly **one OFF and one ON**, both returning 0;
OFF was observed, the restore child completed normally, and the controller
exited 0. The setter-return log timestamps are 1.761 seconds apart; this does
not measure the monitor's physical blackout duration. Three stable reads and
two separate read-only implementations confirmed the restored display:

- `PG32UCDP`, `\\.\DISPLAY1`, target 4353, both adapter LUIDs 90908/0;
- same monitor device path and connector, flags `0x73`, HDR mode 2;
- HDR enabled/active, 10 bits per channel, WCG user preference 0;
- the WPF display service also reports the unchanged SDR white level, 240 nit.

| Observation | Before | After | Meaning |
| --- | --- | --- | --- |
| Foreground VRR getter samples | 50/50, HotS PID 29940 | 50/50, HotS PID 29940 | All getters and VBlank queries succeeded on display `0x80061082` |
| VRR flags | `0x12` throughout | `0x12` throughout | Enabled/Requested remain clear; active VRR is not established |
| Driver VBlank counter rate | 240.023 Hz | 240.054 Hz | Driver-side counts, not optical adaptive-scanout proof |
| PresentMon records | 1195 | 1194 | Same PID and swapchain `0x000001DA3C733820`, all `Composed: Flip` |

Timing limits matter: the fresh VRR windows end about 23.47 seconds before
and begin 23.13 seconds after the cycle; they do not cover the transition.
The earlier PresentMon baseline is approximately **28 minutes** before the
cycle, while the after trace is about 23–33 seconds afterwards. This is not a
matched frametime or latency A/B test. Both traces mark every record Dropped=1
and have no usable display-latency values. Their stdout ETW status reports
zero lost events/buffers/overflowed Presents, but that does not prove complete
display-event correlation or that all physical frames were dropped. The
observed HotS menu remains visible in the saved after screenshot; screenshots
do not measure the gamma curve, HDR luminance, frame pacing or latency.

Final process snapshots retain the exact prior creation times for HotS PID
29940, DWM PID 31032 and HDRCalibrate PID 19556; all report responding. No
cycle helper or owned HDR-cycle ETW session remained at the final check.
The queried System/Application logs from 15:11 to approximately 15:14 local
contained no relevant display-driver warning/error or DWM/HotS/application
crash event. No selective veto, gamma application, game modification, driver
profile write, installation or release occurred in this cycle test. The v3
hook remains pinned/pass-through; this is not an unload claim.

Evidence under `artifacts/selective-dwm-20260906/hdr-cycle/`:

- `bin/build.json`, `bin/offline-20260906-151401/test-results.json`;
- `wmi-synthetic-20260906/` normal/crash/late-OFF logs;
- `live-20260906/authorized-cycle.jsonl` and independent display snapshots;
- `live-20260906/after-comparison-summary.json`, raw VRR/PresentMon logs;
- `live-20260906/hots-before.jpg`, `hots-after.jpg`;
- `live-20260906/final-process-state.json`, `post-cycle-events.json`.

**Result:** HDR was restored and protected applications were preserved. The
cycle did not re-establish the earlier VRR status. HotS gamma with verified VRR,
HDR isolation and low latency remains unimplemented/unaccepted. Do not enable
it automatically or promote these diagnostic results to a working feature.
