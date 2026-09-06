# Selective DWM prototype library

This x64 DLL is a bounded experiment for the exact local `dwmcore.dll`
`10.0.26100.8875`, SHA-256
`825726F26BD6B5A02B49F6145963B54D537D3D1239055DDA28D1FB0B3E864EF3`.
It does not implement a gamma curve, change a color profile, or prove that VRR
continues to operate. The controller and live acceptance evidence are separate.

`DllMain` does nothing. `SelectiveDwmStart` explicitly starts in observer mode;
it rejects an ordinary non-DWM process and verifies the system DLL path, whole
file hash and all twelve code contracts before installing two MinHook hooks.
The file hash, code contracts and private layout are not portable to another
Windows build. An unknown concrete implementation is unsupported.

## Current behavior

The candidate hook preserves all thirteen x64 arguments (including `this`),
the native signed 32-bit HRESULT and LastError. An explicitly armed request can
return the native ordinary candidate rejection value `S_OK` without recording
that candidate. The native signature has no output parameters. The original
function has ordinary early rejection branches before candidate recording and
returns zero at RVA `0x2680FA`.

Veto requires all of the following at the actual callback:

- A stable request sequence, matching version/size, nonce and fixed target and
  controller identities; both retained process handles must remain unsignaled.
- An issued QPC lease of at most five seconds with `issued <= now < deadline`.
- The recognized native visual HWND/PID getters, agreement with
  `GetWindowThreadProcessId`, and exact agreement with the requested HWND/PID.
- Current source color space `RGB_FULL_G22_NONE_P709` (DXGI value 0) and DXGI
  format 28, 87 or 88. FP16/scRGB, PQ, HLG, wide gamut, limited range, sRGB-typed
  formats and unknown metadata pass through.
- An exact `CGlobalCompositionSurfaceInfo` primary vtable and getter contracts,
  nonzero current BindId, and current realization equal to the actual
  candidate realization.
- Three selected SDR observations in observer mode, followed by an explicit
  controller request for that exact visual/surface/bind tuple.

`EnsureIndependentFlipState` is observed. Its original `enable` argument is
always forwarded unchanged. A fresh current source description is inspected
when available, and must agree with the plane's color-space attribute. The
library intentionally does not call `DisableIndependentFlip` or rewrite plane
flags. Removing a native candidate may cause DWM's own obsolete-plane cleanup
to call `DisableOverlay`, which in turn can disable that BindInfo's independent
flip. Whether this occurs for a particular present path requires live evidence.

The source descriptor is read using the statically verified realization thunk
and exact `CD3DSurface::IPixelFormat` or `CD2DBitmap::IPixelFormat` leaf. Their
different field layouts are selected only by the exact vtable/getter pair.
No unknown virtual pixel-format
getter is called. Private reads are guarded against access violations and
in-page errors; uncertainty returns to the native function.

## Lifetime and stop contract

The shared protocol is `../include/SelectiveDwmProtocol.h`, version 2: Request
128 bytes, Sample 112 bytes, Telemetry 352 bytes, SharedState 480 bytes,
StartConfig 304 bytes. The mapping name must begin with
`Local\HDRCalibrate.SelectiveDwm.`.
The controller must provide a new mapping and nonce for each session.
`latestTarget` retains completed selected candidate samples only, so the
immediately following IndependentFlip callback cannot erase the controller's
arming evidence. `latest` continues to report either hook point. The field
named `bind` contains the uint64 result of `GetCurrentBindId`, not a CBindInfo
object pointer; it is never dereferenced.

StartConfig contains two borrowed process handles previously duplicated by the
controller into DWM with only `PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE`.
The DLL makes its own local copies and checks their process IDs, creation times,
liveness and sessions. The controller releases the borrowed remote handles
after Start finishes, regardless of success; it must retain them if the remote
call times out and may still be using them. The DLL owns only its local copies.
Foreign sessions are read using the retained handles and the same native
four-byte query used by the installed `ProcessIdToSessionId`; the DLL does not
reopen those PIDs or enable a privilege in DWM.

`SelectiveDwmStop` marks the current session stopped and removes it from the
active pointer. The DLL, native trampolines, mapping and process handles remain
pinned until DWM exits. A new explicit start can select another target with a
new immutable session; at most sixteen sessions are retained. This avoids
freeing data that an in-flight compositor callback could still reference.
There is no autostart, service, scheduled task, registry setting or automatic
DWM restart. `FreeLibrary` is not supported cleanup for an installed hook.

Stop and lease expiry make subsequent hook decisions pass through. They cannot
recover a compositor thread already blocked inside native code or an OS hook
installation operation. Callback-stage telemetry is published before native
ownership getters, private metadata reads and the original function, so the
controller can distinguish an entered callback from a completed one.

Telemetry sample readers use the same sequence protocol as requests. A sample
writer makes a single compare/exchange attempt; it drops a contended sample
instead of waiting. Request readers make at most three attempts. The production
callback contains no filesystem logging, heap allocation, nonzero wait, or
communication with the game process. Its process handles are metadata/liveness
handles only.

## Build and verified host tests

The root build supplies official MinHook 1.3.4, pinned upstream commit
`c3fcafdc10146beb5919319d0683e44e3c30d537`, as an x64 static library. This script
does not download dependencies or install anything:

```powershell
& .\tools\SelectiveDwm\Hook\BuildHook.ps1 -OutputDirectory .\artifacts\selective-dwm-20260906\offline-check -RunTests
```

The default output (do not overwrite while loaded) is
`artifacts\selective-dwm-20260906\v3\bin\HDRCalibrate.SelectiveDwmHook.dll`.
Do not rebuild over a DLL that is already loaded into DWM. The verified frozen
version 3 prototype built on 2026-09-06 has SHA-256
`128556F6904418F928D72875B974674FC0A0BF1C90209570D7E6D0D1AB6B0596`.
It retains wire protocol version 2. Following explicit user authorization,
the root controller deployed this exact frozen image into fresh DWM PID 31032
on 2026-09-06. The completed sessions are stopped/pass-through, but the image
remains pinned. See [live results](../TEST-RESULTS-20260906.md).
`-HostTestsOnly -RunTests` rebuilds and runs only the test executable while
leaving that DLL file untouched.

MSVC x64 `/W4 /WX /O2 /MT /guard:cf` plus linker `/DYNAMICBASE /NXCOMPAT
/CETCOMPAT /GUARD:CF` built with no warnings or errors. `HookSelfTest.exe` passed
72 checks. It applies real MinHook trampolines to two synthetic host functions
and exercises the production dispatch and policy. Coverage includes all
arguments and return/LastError preservation, observer gating, selected SDR
rejection, every excluded source class, malformed and expired leases, changed
identity/bind, signaled lifetime handles, read faults, contended telemetry,
unchanged native independent-flip policy, stopped-session forwarding, retained
session replacement and a deterministic two-thread Stop/lifecycle race. The
actual production DLL is also loaded into the ordinary test host and rejects
Start before installing hooks. Actual metadata-only duplicated handles verify
the process ID, creation time and native session query against Win32.
Production `ReadPixelFormat` is exercised against both concrete in-memory
layouts, with conflicting values at the other layout's offsets; mixed
vtable/getter pairs and unknown or faulting objects are rejected. Another
regression verifies that an IndependentFlip sample cannot overwrite a completed
candidate's arming evidence.

The host tests do not load DWM, read or write game process memory, start or stop
a game, inject anything, or verify desktop color/VRR behavior. Their synthetic
metadata is explicitly confined to a separately compiled test executable; the
production DLL contains no test metadata override.

The earlier version 1 file remains separately at the original output path,
SHA-256 `91314062523D50793931E1D1478470B4ECE227C47594F031B7CED0110E042C0B`.
Its first controller-run fixture observation returned `ERROR_INVALID_OWNER`
(1307) before hook installation and before telemetry initialization; its
candidate and independent-flip counters remained zero. That result alone did
not identify which combined identity check failed. Version 2 supplies handles
instead of opening foreign PIDs under DWM's token and publishes distinct
startup stages and the first failing Win32 status. Any live result for version
2 must come from the root controller's later evidence, not these host tests.

The version 2 file under `v2\bin` remains unchanged at SHA-256
`767DDA29AB31C0C4F41C571EB2FEC82668FD4EE43CD0B334F6BA93B114538E44`.
Its observed HotS source passed HWND/PID/current-surface/BindId checks but was
rejected as unsupported pixel metadata. A bounded DWM-only read then identified
the exact `CD2DBitmap` leaf at vtable RVA `0x2F97E8`, getter RVA `0x1BF4A0`.
Its complete 24-byte implementation reads three DWORDs from interface offsets
`0x50`, `0x74`, `0x90` and writes the twelve-byte descriptor. Independent PDB,
file disassembly and 140 bytes of bounded live reads agreed: current format 87,
alpha mode 3 and color space 0. Version 3 adds that exact concrete leaf without
enabling arbitrary virtual methods or changing SDR eligibility.

`InspectSurface.ps1` provides the narrow read-only diagnostic. It verifies the
exact DWM PID/creation/path, on-disk dwmcore hash, current GCSI vtable, all reached
vtable/getter pairs and their bytes. It caps reads at 32 calls and 4096 bytes,
opens only the selected DWM with QUERY_LIMITED_INFORMATION and VM_READ, and
performs no privilege adjustment, target write or remote function call.

Version 2 deliberately exposes no detach or hot-upgrade function: Stop keeps
its trampolines installed and turns them into pass-through calls. Therefore
version 3 must not be loaded as competing hooks into that same DWM instance.
Deploy it only in a new, separately authorized DWM session, or after a separately
designed and verified upgrade mechanism. The later authorized restart removed
the previous v1/v2 mappings through normal process termination; no unsafe
in-place unload or competing-hook replacement was performed.

Static ABI evidence and limitations also appear in
`artifacts/heroes-sdr-feasibility/dwm-mpo-route-research.md` and the root
controller's implementation/acceptance documentation.
