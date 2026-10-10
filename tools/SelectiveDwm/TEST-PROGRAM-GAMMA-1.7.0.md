# Program-list gamma integration — 1.7.0, 2026-09-06

Local implementation/build verification, **not a combined live HotS gamma,
HDR, VRR or latency acceptance test**. No new GitHub release was published for
this change. The installed application was not replaced or launched by these
tests. No game process/file, live DWM hook, display setting or driver setting
was changed by the integration tests.

## Verified

| Check | Result | Boundary |
| --- | --- | --- |
| Managed Release build and installer build | 0 errors, 0 warnings | WPF/.NET Framework 4.8.1, x64, version 1.7.0 |
| Application regression | 78 passed | Repeated with the exact staged installer application bytes |
| Native controller host | 82 passed | Owned process handles, synthetic requests, actual local pipes, bounded leases and surface rejection |
| Native hook host | 72 passed | Synthetic host; unchanged production hook source and protocol |
| Packaged native CLI | 70 passed | Parser/dry-run/local export inspection and negative metadata checks before DWM |
| Packaged controller / .NET pipe | 3 passed | STOP before GO, wrong process generation and wrong HWND; no DWM access |
| UI component rendering | Ready, active and minimum-size layouts checked | Offscreen WPF content tree and fake session controller; no real gamma application |
| Native bundle integrity | Passed | Embedded SHA-256 manifest, x64 PE files, license and write locks |
| Incremental build | Passed | Same source/output hashes and timestamps on repeated build |
| DWM read-only preflight | Passed | 4 shader targets, 16 constant vectors, 228 planned changed bytes; DWM identity retained during check |

New regressions include no autoactivation from a saved checkbox, exact
process/window/settings/Windows-generation binding, rejection of incomplete
results, atomic Stop versus test-proof commit, terminal-event activity
revocation, stale/future telemetry exclusion, late UAC completion cleanup and
responsive versus deliberately blocked owned Dispatcher heartbeats.

Final managed log and offscreen renders:
`artifacts/program-gamma-1.7.0-final-20260906/tests.log` and
`artifacts/program-gamma-1.7.0-final-20260906/program-gamma-ui/`.
Native host logs: `artifacts/program-gamma-native-20260906/bin/`.
Packaged pipe report: `artifacts/program-gamma-native/1.7.0/final-pipe-tests/summary.json`.
These artifact paths are generated local evidence, not runtime dependencies.

## Local artifact identity

- Installer: `installer/artifacts/output/HDRCalibrate-Setup-v1.7.0-x64.exe`.
- Installer SHA-256: `4AF2B802FBD1745E5E4B5A0593A711C56A8330E84FCADDDEA1A5FB26295474E5`.
- Staged/tested app SHA-256: `1D9CF57C6B379B6EE9EC184D9417B471458528CACC4A005DB58F8F26073849D2`.
- Packaged controller SHA-256: `A1B7B2334BD5A94390341F2CCE5B1FCD2C99E714616516DFCB5723F30E51633F`.
- Packaged hook SHA-256: `990459305A3F4948C228A6DAA71E70E456614C1212AFE531A5A130299698CE16`.

The installer is not Authenticode-signed. Install/uninstall was compiled and
reviewed but not live executed. A loaded hook DLL may remain with its license
after uninstall; no DWM restart or DLL unload is performed to remove it.

## Remaining live acceptance

The UI now supplies the manual gamma-selection workflow for arbitrary local
EXEs, but only one confirmed SDR window/surface at a time. It adopts the
already committed global SDR correction rather than a per-app gamma value.
The native selection test proves candidate veto/identity/withdrawal only.
Visible transfer-curve changes, SDR/HDR isolation on the real output,
composition-path use, VRR and lowest latency still require a combined
application/game hardware test. Historical v3 findings remain in
`TEST-RESULTS-20260906.md`; they must not be relabeled as integrated-path proof.
