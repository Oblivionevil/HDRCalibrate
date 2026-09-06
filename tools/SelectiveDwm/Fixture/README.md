# Isolated DX11 SDR/HDR color fixture

`HDRCalibrate.ColorFixture.exe` owns one Win32 window and a DX11 flip-discard swap chain. It has no foreign-process, desktop-capture, hook, display-setting, profile, registry, game-file or vendor-application dependency. The default visible run expires after 10 seconds. `WM_CLOSE` closes only its own window. A watchdog exits only the fixture if startup takes more than 15 seconds or cleanup/presentation stalls beyond the selected duration plus 2 seconds. OS scheduling is not a hard real-time guarantee.

Build in PowerShell:

```powershell
& .\tools\SelectiveDwm\Fixture\Build-Fixture.ps1
$fixture = '.\tools\SelectiveDwm\Fixture\bin\HDRCalibrate.ColorFixture.exe'
& $fixture --self-test
& $fixture --gpu-self-test --warp
& $fixture --gpu-self-test
& .\tools\SelectiveDwm\Fixture\Test-Fixture.ps1 -IncludeHiddenSmoke
```

The build locates Visual Studio C++ with `vswhere`, prefers installed MSVC 14.44, invokes `VsDevCmd`, and compiles x64 with `/W4 /WX`, static CRT and control-flow guard. `-VisualStudioPath`, `-ToolsetVersion` and `-OutputDirectory` are optional overrides. Builds do not stop an already running fixture; use a different output directory if its executable is locked.

`--self-test` creates neither a window nor a GPU device. It verifies parser bounds, patch coverage, sRGB and power 2/2.2/2.4 anchors, Rec.709-to-Rec.2020 neutral preservation, and independently specified PQ anchors. `--gpu-self-test [--warp]` creates no window or swap chain: it renders and checks every RGBA channel of its own 64x24 generated offscreen textures in all three formats. Storage quantization is allowed. These are content-generation tests, not output/color-management/capture tests.

`Test-Fixture.ps1` saves raw JSONL, stderr and a summary under `test-results`. It runs these tests plus invalid CLI cases. `-IncludeHiddenSmoke` adds 500-ms invisible SDR/scRGB/PQ swap-chain runs at 59.94 FPS and an odd 321x181 client size. It never shows a fixture window. `-SkipHardware` retains the WARP tests. Unsupported requested presentation color spaces are recorded as skipped smoke coverage, not evidence of a successful render.

Visible runs, only during a coordinated desktop test:

```powershell
& $fixture --mode sdr --duration-ms 10000 --fps 60 --width 800 --height 450 --x 64 --y 64 --no-activate
& $fixture --mode scrgb --duration-ms 10000 --fps 60 --width 800 --height 450 --x 920 --y 64 --no-activate
& $fixture --mode pq --duration-ms 10000 --fps 59.94 --width 800 --height 450 --x 64 --y 600 --no-activate
```

`--hidden` creates an invisible window for bounded swap-chain initialization/presentation smoke checks. Hidden/occluded presentation is not evidence of visible scanout. `--borderless` creates only a borderless window at the requested position/size; it never requests exclusive fullscreen. `--no-activate` uses `WS_EX_NOACTIVATE` and `SW_SHOWNOACTIVATE`. `--warp` explicitly chooses software rendering; hardware creation failure never silently falls back. Duration is 1..600000 ms, FPS 1..1000 (fractional accepted), client dimensions 64..4096, and positions -32768..32767. There is no indefinite mode.

## Source patterns

By default all frames repaint the same 8x3 patch grid without temporal dither, animation, shader gamma correction, blend, tone mapping or interpolation. Integer cell edges cover every source pixel, including odd dimensions. The exact client-relative rectangles and floating-point RGBA writes are emitted as `patch` JSONL events. No text is drawn into the client area.

`--animate-marker` optionally adds one 8x2 pixel marker in client rows `y=[0,2)`. It moves seven pixels per submitted frame, wrapping within the source width, and alternates between the row-1 magenta and cyan source colours. The entire patch grid is repainted before the marker, restoring its previous position. Every patch centre is outside the marker strip, including at the minimum 64-pixel client height; exclude the top two rows from other color measurements. The `frame` events record enabled state, phase, rectangle, source sRGB code and exact encoded RGBA values. Default rendering remains unchanged. This provides changing content for coordinated presentation experiments; it does not establish visible scanout or VRR. Both marker phases and restoration of the prior position are checked on the fixture's own textures by GPU self-tests in all three formats.

| Mode | Swap-chain format | Explicit color space | Values |
| --- | --- | --- | --- |
| `sdr` | R8G8B8A8_UNORM | RGB_FULL_G22_NONE_P709 | Encoded sRGB values written directly into UNORM; no sRGB RTV conversion. |
| `scrgb` | R16G16B16A16_FLOAT | RGB_FULL_G10_NONE_P709 | Linear Rec.709 nominal nits / 80. |
| `pq` | R10G10B10A2_UNORM | RGB_FULL_G2084_NONE_P2020 | Rec.709 linear RGB converted to Rec.2020, then ST.2084 encoded relative to 10000 nits. |

- Row 0: grayscale source codes 0, 16, 32, 64, 128, 192, 235, 255 divided by 255.
- Row 1: red, green, blue, yellow, cyan, magenta, encoded neutral 0.5, encoded warm RGB (0.75, 0.5, 0.25).
- SDR row 2: near-black grayscale codes 1, 2, 4, 8, 12, 24, 48, 96 divided by 255.
- scRGB/PQ row 2: absolute neutral references 0, 80, 160, 320, 500, 600, 800, 1000 nominal nits. scRGB reaches 12.5, above its nominal 80-nit white. `source_srgb_code` is inapplicable (zero placeholder) for `kind=absolute_hdr_neutral`; use `linear_rec709_nits` and `written_rgba`.

`--reference-white-nits` defaults to 80 and affects HDR modes' first two rows only. To compare them to SDR brightness under Windows HDR, supply the independently read Windows SDR white level; do not assume the default equals the desktop slider. The emitted `windows_advanced_color.sdr_white_nits` reports the current read-only Windows query. The app never changes that setting. The last HDR row is always an absolute reference, unaffected by this option.

The `gamma_reference` events list `pow(code, 2.0)`, `pow(code, 2.2)`, `pow(code, 2.4)` and piecewise sRGB decode separately. These describe mathematical candidate curves, not observed DWM behavior or a selected correction. For encoded 0.5 the linear values are respectively 0.25, 0.217637640824031, 0.189464570813800, and sRGB 0.214041140482233. An 8-bit stored midpoint 128/255 is a different input. SDR writes quantize to 8 bits; PQ writes to 10 bits; scRGB writes to FP16.

## Evidence and limitations

The first `window_created` event identifies PID/HWND, mode, position and dimensions before swap-chain initialization. Wait for `ready` before evaluating rendering. The title also names the mode. `output` reports `IDXGIOutput6::GetDesc1` color space and metadata luminance; `windows_advanced_color` separately reports supported/enabled flags and SDR white. A failed query is explicitly marked and does not establish HDR support. The output color space being `RGB_FULL_G2084_NONE_P2020` is logged as the HDR-active signal. Advanced Color alone can also apply to SDR displays. A successful FP16 allocation or `SetColorSpace1` does not prove visible HDR output. Unsupported requested color spaces exit 3 without substitution. The app uses no explicit HDR10 metadata override.

Presentation uses two buffers, `FLIP_DISCARD`, a frame-latency waitable object and `SetMaximumFrameLatency(1)`. The app waits before every frame including the first. If DXGI reports tearing support, it uses `ALLOW_TEARING` with sync interval 0; otherwise it uses sync interval 1 and reports this. It does not force independent flip, MPO, VRR, a display mode or a refresh rate. The QPC scheduler uses its own high-resolution waitable timer (reported ordinary-timer fallback if unavailable), without changing the global timer resolution. It is a requested cadence; message pumping, the swap-chain waitable object, the driver and logging can change observed cadence.

Each `frame` records QPC schedule, wait, render and Present boundaries, the HRESULT and occlusion. This supports correlation to external ETW/PresentMon evidence. It is CPU submission timing; it is not GPU duration, scanout, click-to-photon latency, a VRR measurement or optical color validation. `complete` carries frame/occlusion/timeout counts and exit reason. JSONL is flushed per event for controller synchronization; this logging cost is part of the fixture workload. Compare equivalent runs and use an external display/ETW measurement for presentation claims.

Exit codes: 0 completed/test passed; 2 invalid arguments/API/test failure; 3 requested color space cannot present; 124 watchdog self-exit.

Public API references:

- [Microsoft: DirectX with Advanced Color](https://learn.microsoft.com/en-us/windows/win32/direct3darticles/high-dynamic-range)
- [Microsoft: frame-latency waitable object](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_3/nf-dxgi1_3-idxgiswapchain2-getframelatencywaitableobject)
- [Microsoft: variable refresh rate display requirements](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/variable-refresh-rate-displays)
- [Microsoft: ID3D11DeviceContext1::ClearView](https://learn.microsoft.com/en-us/windows/win32/api/d3d11_1/nf-d3d11_1-id3d11devicecontext1-clearview)
