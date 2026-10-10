# SDR shader coverage correction (2026-09-25)

Status: source/offline regression and bounded live apply/reset verified;
visible gamma, HDR isolation and VRR not accepted. Not installed or published.

## Finding

The previous manifest validated six SDR conversion exports but marked only the
four FL9.1/FL9.3 exports as patch targets. Disassembly of the exact current
System32 dwmcore.dll also shows the FL10 alpha and opaque exports implementing
the sRGB transfer function (offset 0.055, scale 0.94786733, exponent 2.4 and
breakpoint 0.04045). These were left at Windows defaults by every previous
gamma setting. This establishes a shader coverage gap, not that HotS executes
these exports: actual execution and final display output remain unverified.

The candidate hook skips CheckAndRecordOverlayCandidate and returns S_OK.
Its veto count is not a count of changed presentations; it never calls the
original on those invocations and cannot know whether the native path would
have accepted the candidate. Independent-flip observation remains pass-through.

## Change and checks

- All six exact-hash SDR exports are now targets: 24 constant vectors across
  three profile libraries. No native hook capability/allowlist change.
- Non-SHDR chunks are checked for byte identity using the manifest-validated
  chunk list. FL10 has no Aon9 chunk; downlevel Aon9 remains checked unchanged.
- All six outputs retain fixed sizes, chunk offsets, reflection ABI and valid
  checksums; D3DCompiler accepts the patched functions and profile libraries.
- Build: zero errors/warnings. Full app regression: 91 passed.
- 440 non-target shader blobs remain byte-identical at gamma 2.0/2.2/2.4.
- Boundary, mapped-image fingerprint and Windows-update/startup gates passed.
- Read-only `Verify-SafetyGate.ps1 -BuildDirectory artifacts/fl10-sdr-coverage-check`
  passed against the actual system image: six targets, 24 vectors, 341 changed
  bytes planned at gamma 2.4, with DWM identity unchanged. No writes were applied.
- Build-only PowerShell 5.1 File.Replace null-string binding was corrected;
  subsequent fresh build and cached build both succeeded.

Commands:

```powershell
dotnet build tests/HDRCalibrate.Tests/HDRCalibrate.Tests.csproj -c Release -p:Platform=x64 --no-restore -o artifacts/fl10-sdr-coverage-check
./artifacts/fl10-sdr-coverage-check/HDRCalibrate.Tests.exe
```

The isolated output is not a release. No installed files, running DWM/game,
gamma settings or persistent approvals were replaced. Applying this changed
shader plan uses the existing engine's DWM restart; an ordinary five-second
test resets to unpatched Windows, not the previously applied gamma 2.2.
Therefore the prior no-DWM-restart test procedure cannot be reused for this
change. A supervised live test must explicitly account for restart and prior
calibration restoration. Gamma/VRR/latency acceptance is still outstanding.

The persisted active calibration was re-read after the preflight: gamma 2.2,
brightness 0, neutral color controls, DWM PID 13304 / start ticks
639259475157214462. The installed DWM assembly remains distinct from the new
isolated build. Restart permission is pending; automated goal continuation is
not an answer to that permission request. Normal TimedTest performs both an
apply restart and an unpatched recovery restart, so approval must cover both.

## Authorized live test, 17:29 UTC

The user explicitly approved the live test after disclosure of both restarts
and removal of the previous gamma setting. This supersedes pending permission
above. The isolated helper ran one TimedTest at gamma 2.4, brightness zero.

- Request/response: `%LOCALAPPDATA%\HDRCalibrate\Requests\fl10-live-20260925-01.request.xml`
  and `fl10-live-20260925-01.response.xml`.
- Six shaders, 24 vectors, 341 bytes on eight pages written and read back.
- Activation: 2026-09-25T17:29:56.7046743Z, DWM PID 13508.
- Watchdog reset timestamp: 2026-09-25T17:30:01.7268972Z; reported test duration
  5005 ms. Recovery DWM PID 2780, start ticks 639259542020238940.
- Recovery verified the original six shader blobs byte-for-byte in System32's
  mapped module. All helper processes exited. HotS retained PID 19200 and its
  original start time; it was not stopped or restarted.
- Baseline window screenshot was intact. The capture inside the active interval
  (17:29:58.485-17:30:00.642 UTC) was black except pointers. Subsequent captures
  of HotS and HDRCalibrate also stayed black after the verified unpatched reset,
  including re-selection and a fresh JS session. The user independently
  confirmed the actual screen was normal after recovery. Thus those captures
  cannot prove physical black output or visible gamma, and the visual test is
  inconclusive. No additional Apply/Reset was dispatched to fix capture.
- No relevant Application 1000/1001 crash event was returned by the short
  post-test query. This does not substitute for visual or latency acceptance.

The old persisted `dwm-state.xml` still identifies PID 13304 and gamma 2.2;
it is historical, not current activation evidence. Current DWM is unpatched.
The formerly pinned candidate hook disappeared with the authorized restart;
its prior same-generation reuse reports no longer authorize a new session.

## Watched repeat: no visible HotS difference

The user authorized a second test, was given a five-second warning to foreground
HotS and watch its dark areas, and then explicitly reported "kein unterschied".
Response: `%LOCALAPPDATA%\HDRCalibrate\Requests\fl10-live-20260925-02.response.xml`.
The helper exited 0, patched/read back six shaders and 341 bytes, and recovered
after 5007 ms to original shader bytes in DWM PID 31372. HotS retained PID 19200
and its original start time. No persistent correction remains applied.

**Visible HotS gamma acceptance failed in this test.** This supersedes the first
test's lack of a user observation; it does not establish that the FL10 extension
is sufficient. Neither timed shader test reinstalled the selective candidate
hook after the DWM restart. Current module inspection found no Selective hook
in DWM, and the installed managed forcing gate still supports only 8875.
Consequently this was not a simultaneous gamma-plus-selective-forcing test.
The earlier veto tests used the old four-shader correction; the new six-shader
tests had no veto. Do not infer combined-path success or failure from either.
Any combined experiment must bind a fresh DWM generation and retain an
independent timed recovery; old PID-bound observer evidence cannot be reused.
