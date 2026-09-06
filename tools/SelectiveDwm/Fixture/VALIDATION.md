# Fixture verification, 2026-09-06

Built with the installed Visual Studio 18 Community, MSVC 14.44.35207 x64, `/W4 /WX /O2 /MT /guard:cf /sdl`. Build succeeded without warnings. Executable: `bin/HDRCalibrate.ColorFixture.exe`, 231424 bytes, SHA-256 `3BDA2D73953862E07666294196A3CA48AEF94BB6BD295580B85DBDA9524B8D7A`.

Commands:

```powershell
& .\tools\SelectiveDwm\Fixture\Build-Fixture.ps1
& .\tools\SelectiveDwm\Fixture\Test-Fixture.ps1 -IncludeHiddenSmoke
```

Evidence: `test-results/20260906-124130-272/summary.json` and adjacent raw `.jsonl` / `.stderr.log` files. All 11 automated cases passed. No visible window was launched.

- Pure math/CLI/geometry: 118 checks, zero failures, no window or GPU device.
- WARP: 18432 RGBA channel comparisons across SDR UNORM, scRGB FP16 and PQ UNORM10 offscreen textures, zero failures.
- RTX 5090: the same 18432 channel comparisons, zero failures. All reads were of the fixture's own generated textures, without any display/desktop readback.
- Invalid duration, excessive duration, duplicate mode, nonfinite FPS and unknown mode were rejected with exit 2 before creating a window.
- Hidden SDR/scRGB/PQ initialization and presentation all exited 0, each with 30 Present calls in approximately 500.3 ms. The actual swap-chain descriptions reported 2 buffers, flip discard, frame-latency limit 1, waitable object and tearing flag. Every logged window state remained invisible.

| Hidden run | Elapsed ms | CPU Present interval median ms | Output color space from GetDesc1 |
| --- | ---: | ---: | --- |
| SDR | 500.2973 | 16.7699 | RGB_FULL_G2084_NONE_P2020 |
| scRGB | 500.3627 | 16.5988 | RGB_FULL_G2084_NONE_P2020 |
| PQ | 500.3258 | 16.6818 | RGB_FULL_G2084_NONE_P2020 |

Windows reported Advanced Color supported/enabled, 10 bits, and SDR white 240 nominal nits during these runs. This is a read-only, time-specific system snapshot. The app's own high-resolution waitable timer succeeded; global timer resolution was not changed. The frame timing is CPU submission only. This verification does not establish visible HDR luminance, DWM transformation selectivity, preservation of native HDR content under an external hook, MPO/independent-flip behavior, active VRR or end-to-end latency. Those require separate coordinated live measurements.

## Optional moving-content build, 2026-09-06

The later `--animate-marker` enhancement was built separately to `artifacts/selective-dwm-20260906/fixture-moving/bin/HDRCalibrate.ColorFixture.exe`: 233984 bytes, SHA-256 `238DA49635DAE0CDEC584B2C69C4AA84E9C189AEFBCD77358C8A7E903309DB95`. The original executable in `tools/SelectiveDwm/Fixture/bin` was not overwritten; its original SHA-256 above was rechecked.

```powershell
& .\tools\SelectiveDwm\Fixture\Build-Fixture.ps1 -OutputDirectory .\artifacts\selective-dwm-20260906\fixture-moving\bin
& .\tools\SelectiveDwm\Fixture\Test-Fixture.ps1 -FixturePath .\artifacts\selective-dwm-20260906\fixture-moving\bin\HDRCalibrate.ColorFixture.exe -OutputDirectory .\artifacts\selective-dwm-20260906\fixture-moving\tests
```

This follow-up verification created no window or swap chain. All 8 offline cases passed; raw JSONL and summary are under `artifacts/selective-dwm-20260906/fixture-moving/tests`.

- 499 CPU checks passed, including CLI opt-in/default behavior, bounded marker geometry, phase/movement and exclusion of all 24 patch centres at minimum, odd and maximum dimensions.
- WARP and RTX 5090 each passed 55296 RGBA comparisons across all three formats: the default static pattern, moving phase 0, and moving phase 1. Every pixel was checked, including restoration of the previous marker position and unchanged patch centres.
- The opt-in marker is 8x2 source pixels in `y=[0,2)`, moves seven pixels per submitted frame and alternates magenta/cyan. Per-frame JSONL includes its rectangle, phase, source code and encoded RGBA writes. Default image generation is unchanged.

Add `--animate-marker` to a coordinated visible run at `--fps 120` or `--fps 137` to generate changing content. No visible presentation or VRR claim follows from these offline tests.
