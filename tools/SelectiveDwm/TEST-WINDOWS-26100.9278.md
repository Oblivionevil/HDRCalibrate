# HDR Calibrate 1.7.1 — Windows 26100.9278 compatibility

Verified locally on 2026-09-22, Windows build 26100.9457, HDR-active PG32UCDP.
This is a source/build, offline shader and read-only DWM verification, **not a
live gamma, VRR, latency or installer-installation acceptance test**.

## Exact global SDR shader identity

- File version: `10.0.26100.9278 (WinBuild.160101.0800)`.
- System `dwmcore.dll` SHA-256:
  `40579ECFA394395677FEC84014C32BA5495F334B6D96C5653FD5CAA129DC1222`.
- All six SDR function DXBC checksums, sizes, chunk orders and reflected ABIs
  remain unchanged. Four downlevel functions remain the only patch targets.
- The library layout changed; replacing only the system-file hash would not
  have been sufficient. Exact new library checksums and indices are pinned:

| Profile | Library DXBC checksum | Function count | SDR indices |
| --- | --- | --- | --- |
| FL10 | `3191B088A09F9D27061FD85EAB64C6EB` | 133 | 16 / 17 |
| FL9.3 | `97A90639688B740B67F6A6E5F978E579` | 133 | 16 / 17 |
| FL9.1 | `586540FE8C6E9F947BECAA1F60DF7B92` | 101 | 16 / 17 |

Unrecognized whole-file hashes still fail before shader approval. Old app,
helper, Windows or shader-identity approvals do not authorize startup in this
build. No automatic migration or activation was added.

## Separate private hook boundary

Per-program forcing is **not enabled on 9278**. Its existing native private
contract remains pinned to 8875; the whole-file guard and private RVAs were not
changed. Static symbol analysis found relocated functions, but is not runtime
acceptance. New managed gates reject unsupported private builds before target
lookup, native payload access or UAC. Optional program approval cannot turn a
successful global gamma commit into an error. Browser launch profiles remain
independent.

## Results

| Verification | Result |
| --- | --- |
| Managed Release build and installer | 0 errors, 0 warnings |
| App regression, repeated on exact installer payload | 84 passed |
| Native controller host tests | 82 passed |
| Synthetic hook host tests | 72 passed |
| Packaged native parser/export/negative tests | 70 passed, no live DWM access |
| Packaged controller/.NET pipe tests | 3 passed, no live DWM access |
| Read-only live DWM preflight | 4 target shaders, 16 constant vectors, 227 planned changed bytes; DWM identity unchanged |
| Non-target shader byte comparison | All 442 unchanged at gamma 2.0, 2.2 and 2.4 |

The latter is an offline byte comparison, not proof of visible SDR/HDR isolation
or gamma on the actual monitor. No live Apply/Test, DWM restart, app restart,
game change or driver change was performed.

Evidence lives under `artifacts/dwm-9278-analysis/`: `shader-candidates.json`,
`updated-manifest.json`, `preflight.json`, `build.log`, `installer-build.log`,
`installer-payload-tests.log`, `native-host/controller-host-tests.log`,
`hook-host-tests.log`, `cli-tests/summary.json` and `pipe-tests/summary.json`.

## Local package

- Installer: `installer/artifacts/output/HDRCalibrate-Setup-v1.7.1-x64.exe`.
- SHA-256: `024224E7734A1B5A15F4F995F747DBD4D7864D6DC5BE08E15BE155F73BF162F5`.
- Size: 3,161,923 bytes; not Authenticode-signed.
- Exact staged/tested app SHA-256:
  `7913D08F4520C24B5D995302611E4845D4B5A6D5C1AF323B0EC04B2B3ABB49EE`.
- Staged controller SHA-256:
  `73A175651BB21494963933C613E901110DE8326D03A937DD991201FE7CB9759D`.
- Staged hook SHA-256:
  `16A04BEAD634A6943F21A661C9D4AD08CB4F4A137A1B82E320E5999D2302FD7E`.

The installer was built, not installed or published to GitHub. Existing 1.7.0
artifacts and the user's running installation were not replaced.
