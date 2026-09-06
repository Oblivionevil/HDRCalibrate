# Selective SDR composition experiment

Status: **experimental research sources, not an enabled application feature or
ready-to-run HotS release package**.
V3 recognizes the owned SDR fixture and already-running Heroes of the Storm.
Bounded live candidate vetoes now run and expire after five seconds; separate
PQ and scRGB requests are correctly refused. This is **not** proof of the full
feature: after the authorized DWM restart the baseline already lacked active
VRR status, and baseline/recovery PresentMon display tracking is inconclusive.
Gamma is not integrated, and the VRR/latency/recovery acceptance gates remain
unmet. Do not enable this prototype as a normal application feature.

See [the test record](TEST-RESULTS-20260906.md), [native contract](Hook/README.md),
and [isolated SDR/HDR fixture](Fixture/README.md).

## Safety boundary

- Only the exact system DWM process is a hook target. Selected applications are
  identified by HWND, PID, creation time and a retained metadata/liveness handle.
  No game files or application process memory are written; no game is launched
  or terminated. The NVIDIA App is neither used nor required.
- The current Windows `dwmcore.dll` hash and private function contracts are
  allowlisted. Unknown Windows builds, layouts, source types, identities and
  expired requests pass through. Nothing here automatically runs at startup.
- Observe is the default. A candidate veto requires three fresh, matching SDR
  observations and an explicit request. Each active lease is at most 5 seconds.
  PQ, scRGB and unknown metadata are excluded, not relabeled as SDR.
- A candidate is rejected through the compositor's existing native decision
  path. Native independent-flip arguments are not rewritten. Gamma is not
  implemented in this DLL. No monitor/registry/profile/global MPO change occurs.
- Stop and expiry mean pass-through, **not DLL unload**. Code, trampolines and
  session data stay alive until DWM exits. The running v3 image must not be
  overwritten, unloaded or replaced by competing hooks. There is no automatic
  DWM restart. A new image requires an explicitly coordinated fresh DWM session.
- A 5-second lease is not a recovery mechanism for an OS/compositor thread that
  is already blocked. This is not a production safety guarantee.

## Components and reproducible tests

`Build-SelectiveDwm.ps1` builds the native preflight and controller. It verifies
the exact official MinHook 1.3.4 source commit and clean dependency tree before
compiling its library. It does **not** build or launch the hook DLL. The hook and
fixture each have their own build script. Use distinct output directories for
already-running binaries; do not stop user applications to unlock a build.

These sources are separate from the v1.6.1 installer/portable application.
Build prerequisites and local test artifacts are not included in those
packages. In particular, `artifacts/deps/minhook-v1.3.4` must be a clean checkout
of MinHook commit `c3fcafdc10146beb5919319d0683e44e3c30d537`.
`BuildHook.ps1` needs both that source directory and the matching built library;
pass `-MinHookLibrary` explicitly when using a separate controller output.
The historical live runners also require their exact reviewed binaries at the
documented local artifact paths. Do not substitute a new DLL hash to bypass
those live-run checks. The commands below are lab examples, not an end-user
HotS installation procedure.

```powershell
# These are local build/parser/host tests, not DWM activation:
& .\tools\SelectiveDwm\Build-SelectiveDwm.ps1 -OutputDirectory .\artifacts\selective-dwm-20260906\controller-check
& .\tools\SelectiveDwm\Hook\BuildHook.ps1 -OutputDirectory .\artifacts\selective-dwm-20260906\offline-check -MinHookLibrary .\artifacts\selective-dwm-20260906\controller-check\minhook-x64.lib -RunTests
& .\tools\SelectiveDwm\Test-Controller.ps1 -BinaryDirectory .\artifacts\selective-dwm-20260906\controller-check -HookPath .\artifacts\selective-dwm-20260906\v3\bin\HDRCalibrate.SelectiveDwmHook.dll
& .\tools\SelectiveDwm\Test-MovingRunner.ps1
& .\tools\SelectiveDwm\Fixture\Test-Fixture.ps1 -IncludeHiddenSmoke
& .\tools\CompositionProbe\Analyze-CompositionTrace.ps1 -SelfTest
```

`Controller/Preflight.cpp` inspects process protections and ordinary available
access without installing a hook or changing mitigation policies. The
standalone preflight executable accepts `--preflight`.

The newer controller accepts an optional **paired**
`--expected-dwm-pid PID --expected-dwm-creation-filetime FILETIME`. A different
process generation is rejected with exit 21 before any remote allocation,
write or call. A metadata/liveness handle pins the checked generation and its
creation time is checked again on the eventual remote-operation handle. This
is required for a future combined gamma/selection workflow: the hook must join
the exact DWM generation returned by successful gamma application, not whatever
process happens to be current later. Native FILETIME differs from .NET UTC
DateTime ticks; use an explicit UTC `DateTime.ToFileTimeUtc()` conversion.
`--validate-only` parses/prints request fields without opening processes or
loading/inspecting a DLL. Paired identity binding does not itself enable gamma,
prove VRR or authorize a compositor restart. The historical live runners still
use their frozen v3 controller; the new controller is built separately under
`controller-generation-bound/bin` until a combined live test is coordinated.

`Hook/InspectSurface.ps1` is a separate, bounded read-only diagnostic: exact DWM
PID/creation time/system path/core hash; at most 32 reads and 4 KiB; stops at
unknown layouts. It neither calls remote functions nor writes process memory.
Supply a surface address from a current observation; addresses become stale
when a window/surface changes or is destroyed.

`Run-SelectiveExperiment.ps1` is **live experimental activation**, not a unit
test. It accepts only the already-running fixture or HotS, and currently pins
the reviewed v3 hook SHA-256. `-ValidateOnly` performs no activation. The normal
run records a unique PresentMon session with baseline, at-most-5-second lease,
and recovery phases; optional NVAPI/VBlank polling records sampled foreground
and driver status. It starts/stops only helper processes it owns and never
terminates an existing ETW session. Reports retain failed and unarmed outcomes.
Do not change the image pin to bypass a failed acceptance gate.

`Run-MovingFixtureExperiment.ps1` is a separate **live** lab runner pinned to
the reviewed moving-marker fixture and v3 hook. It starts a unique PresentMon
session with `--track_etw_status` before creating its own 33-second fullscreen
SDR fixture, then records baseline / bounded lease / recovery at 120 or 137 FPS.
It rejects an already-running fixture to avoid mixing equal process names.
`-ValidateOnly` starts nothing. All owned helpers terminate; it never restarts
DWM or closes an existing application. Fixed 3840x2160 coordinates make it a
local experiment, not a general monitor-selection feature.
Helper failures, premature target loss and a changed DWM identity fail the
runner, rather than merely appearing in a successful report. Ten offline cases
test the actual pure result gate without executing the live runner body. Normal
PresentMon termination is checked; a forcibly killed monitor is not proof that
its ETW session was cleaned up. Inspect the exact uniquely named owned session
after an exceptional exit; never stop other tracing sessions.

`Diagnostics/ReadNvidiaVrrPolicy.cpp` is an independent **read-only** driver
policy diagnostic, not an activator. It queries base/global/application
profiles using the exact supplied EXE path as a lookup key; it never starts
that EXE or opens its process. The optional `--diagnose-settings` also reads
the five VRR setting names and one standard V-Sync comparison value. Missing
values remain unknown; SDK defaults are not substituted. An exit code of zero
means the query/cleanup completed, not that VRR is enabled or its policy known.

```powershell
& .\tools\SelectiveDwm\Diagnostics\Build-VrrPolicy.ps1
& .\tools\SelectiveDwm\Diagnostics\Test-VrrPolicy.ps1
# Read-only driver query; the first two commands above do not query the driver:
& .\artifacts\selective-dwm-20260906\vrr-policy-sanity\bin\ReadNvidiaVrrPolicy.exe --exe 'C:\Absolute\Application.exe' --diagnose-settings
```

On the tested driver, setting names and the standard V-Sync value are readable,
but the five VRR values are unavailable even in the detected HotS profile.
This is not evidence of a particular fullscreen/windowed G-SYNC configuration.
See the test record for exact return codes and original logs.

## Explicitly authorized HDR-cycle diagnostic

`Diagnostics/CycleWindowsHdr.cpp` is a separate lab diagnostic, **not** a gamma
fix, startup action, or automatic recovery policy. Its default / `--inspect`
mode only reads the unique active `\\.\DISPLAY1` path. The live `--cycle` mode
requires separate user authorization for a brief Windows-HDR OFF/ON cycle.
It never restarts DWM or an application and never changes driver profiles,
game files, WCG preference, or a different display.

The SDK 26100 implementation uses `GET_ADVANCED_COLOR_INFO_2` and
`SET_HDR_STATE`, binding the monitor path, both adapter LUIDs, source/target
IDs and connector. Unknown flags, changed identities and ambiguous paths fail
closed. A same-binary restore child retains the exact controller process
handle and an immutable snapshot. It must prove independence from **all**
Windows jobs and signal READY before the single OFF call. After an eight-second
timeout it terminates only its own controller and waits for confirmed exit
before restoring ON, preventing a late OFF after recovery. The normal path
verifies HDR/WCG state three times before disarming the child. This is not a
guarantee against an OS/driver hang or a disconnected monitor.

```powershell
& .\tools\SelectiveDwm\Diagnostics\Build-HdrCycle.ps1
& .\artifacts\selective-dwm-20260906\hdr-cycle\bin\CycleWindowsHdr.exe --inspect
& .\tools\SelectiveDwm\Diagnostics\Test-HdrCycle.ps1
```

The test script has seven offline cases, including normal completion,
controller crash and a delayed-OFF race; none calls a live display API.
In this automation host, direct descendants remain in a job despite breakaway,
so the guardian correctly refuses before OFF. The completed lab run instead
launched the exact owned helper / unchanged test script through **local**
`Win32_Process.Create`, using `Win32_ProcessStartup.CreateFlags=0x09000000`
(`CREATE_BREAKAWAY_FROM_JOB | CREATE_NO_WINDOW`) and `ShowWindow=0`. No job
limits or security settings were changed. First all three simulations and a
read-only display-context check passed in that launch context; only then was
the separately authorized live cycle run. Child logging requires an inherited
disk-file stdout handle; pipe output is deliberately not inherited.

The one live cycle restored HDR successfully but did **not** establish active
VRR or complete HotS gamma support. See the dated test record below.

## Acceptance still required

1. Stable SDR target identity and source metadata, three candidate observations,
   a nonzero target-only veto count and no post-deadline veto.
2. PresentMon proves actual `Composed: Flip` for the target during the active
   phase, and restoration of its original path after expiry. Merely seeing
   `Hardware Composed: Independent Flip` does not prove shader composition.
3. Separate SDR/scRGB/PQ controls prove HDR and unrelated targets remain
   unchanged during an **actually active** correction. Fixture GPU readbacks
   and ordinary window screenshots do not measure physical HDR luminance.
4. Valid, target-foreground VRR samples throughout stable phases, corroborated
   by VBlank cadence at two in-range frame rates. `DisplayInVRRMode` alone is
   insufficient. Exclude both QPC boundaries of a VBlank call at transitions.
5. Repeated same-scene timing comparisons, including runs without driver
   polling. Present-to-display timing is not input-to-photon latency. Reject
   mixed swapchains/focus changes before comparing distributions.

Windows does not promise that forced compositor shader processing preserves
Independent Flip's latency and VRR behavior. This remains an empirical gate,
not a capability asserted by this prototype. See the
[Microsoft flip-model explanation](https://devblogs.microsoft.com/directx/dxgi-flip-model/)
and [PresentMon metrics](https://github.com/GameTechDev/PresentMon/blob/v2.5.1/README-ConsoleApplication.md).
