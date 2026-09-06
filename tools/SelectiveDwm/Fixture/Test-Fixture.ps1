param(
    [string]$FixturePath = (Join-Path $PSScriptRoot 'bin\HDRCalibrate.ColorFixture.exe'),
    [string]$OutputDirectory,
    [switch]$SkipHardware,
    [switch]$IncludeHiddenSmoke
)

# Only own console/offscreen/explicitly hidden processes are created. No visible
# fixture is launched by this script. All helpers have a bounded lifetime.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$FixturePath = (Resolve-Path -LiteralPath $FixturePath).Path
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot ('test-results\' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$fixtureResults = [Collections.Generic.List[object]]::new()

function Invoke-FixtureTest {
    param([string]$Name, [string[]]$Arguments, [int[]]$AllowedExitCodes = @(0))
    foreach ($fixtureArgument in $Arguments) {
        if ($fixtureArgument -match '["\r\n]') { throw 'Unexpected native argument.' }
    }
    $fixtureStdoutPath = Join-Path $OutputDirectory ($Name + '.jsonl')
    $fixtureStderrPath = Join-Path $OutputDirectory ($Name + '.stderr.log')
    $fixtureNativeArguments = ($Arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $fixtureProcess = Start-Process -FilePath $FixturePath -ArgumentList $fixtureNativeArguments -WindowStyle Hidden `
        -RedirectStandardOutput $fixtureStdoutPath -RedirectStandardError $fixtureStderrPath -PassThru
    try {
        if (-not $fixtureProcess.WaitForExit(18000)) {
            # The retained handle belongs to this invocation's new helper only.
            $fixtureProcess.Kill()
            [void]$fixtureProcess.WaitForExit(2000)
            throw "Own fixture test $Name exceeded its watchdog budget."
        }
        $fixtureProcess.Refresh()
        $fixtureRows = @(Get-Content -LiteralPath $fixtureStdoutPath | ForEach-Object { $_ | ConvertFrom-Json })
        $fixtureErrors = @(Get-Content -LiteralPath $fixtureStderrPath)
        $fixtureResult = [ordered]@{
            Name = $Name
            Arguments = $Arguments
            ExitCode = $fixtureProcess.ExitCode
            Passed = $fixtureProcess.ExitCode -in $AllowedExitCodes
            Output = $fixtureStdoutPath
            ErrorOutput = $fixtureStderrPath
            JsonEvents = $fixtureRows.Count
            Summary = $null
        }
        if ($Name -eq 'math') {
            $fixtureChecks = @($fixtureRows | Where-Object event -eq 'self_test_complete')
            $fixtureResult.Passed = $fixtureResult.Passed -and $fixtureChecks.Count -eq 1 -and $fixtureChecks[0].failures -eq 0 -and -not $fixtureChecks[0].created_windows
            $fixtureResult.Summary = $fixtureChecks
        } elseif ($Name -like 'gpu-*') {
            $fixtureChecks = @($fixtureRows | Where-Object event -eq 'gpu_self_test_complete')
            $fixtureModes = @($fixtureRows | Where-Object event -eq 'gpu_texture_test')
            $fixtureMarkers = @($fixtureRows | Where-Object event -eq 'gpu_marker_test')
            $fixtureResult.Passed = $fixtureResult.Passed -and $fixtureChecks.Count -eq 1 -and $fixtureChecks[0].failures -eq 0 `
                -and -not $fixtureChecks[0].created_windows -and $fixtureModes.Count -eq 3 -and $fixtureMarkers.Count -eq 6 `
                -and @($fixtureMarkers | Where-Object { $_.failures -ne 0 }).Count -eq 0
            $fixtureResult.Summary = @($fixtureModes) + @($fixtureMarkers) + @($fixtureChecks)
        } elseif ($Name -like 'hidden-*') {
            $fixtureReady = @($fixtureRows | Where-Object event -eq 'ready')
            $fixtureComplete = @($fixtureRows | Where-Object event -eq 'complete')
            $fixtureFrames = @($fixtureRows | Where-Object event -eq 'frame')
            $fixtureSwap = @($fixtureRows | Where-Object event -eq 'swap_chain')
            $fixtureOutput = @($fixtureRows | Where-Object event -eq 'output')
            $fixtureWindows = @($fixtureRows | Where-Object event -eq 'window_created')
            $fixtureConfig = @($fixtureRows | Where-Object event -eq 'config')
            if ($fixtureProcess.ExitCode -eq 3) {
                $fixtureResult.Passed = $fixtureResult.Passed -and @($fixtureRows | Where-Object event -eq 'unsupported').Count -eq 1
                $fixtureResult.Summary = 'Requested color space unsupported; smoke skipped, not a render pass.'
            } else {
                $fixtureResult.Passed = $fixtureResult.Passed -and $fixtureReady.Count -eq 1 -and -not $fixtureReady[0].visible `
                    -and $fixtureWindows.Count -eq 1 -and -not $fixtureWindows[0].visible -and $fixtureConfig.Count -eq 1 `
                    -and $fixtureComplete.Count -eq 1 -and -not $fixtureComplete[0].failed -and $fixtureComplete[0].reason -eq 'duration_elapsed' `
                    -and $fixtureComplete[0].elapsed_ms -le 1500 -and $fixtureFrames.Count -gt 0 `
                    -and $fixtureSwap.Count -eq 1 -and $fixtureSwap[0].buffer_count -eq 2 -and $fixtureSwap[0].maximum_frame_latency -eq 1
                $fixtureIntervals = @($fixtureFrames | Where-Object { $_.present_interval_ticks -gt 0 } | ForEach-Object {
                    [double]$_.present_interval_ticks * 1000 / $fixtureConfig[0].qpc_frequency
                } | Sort-Object)
                $fixtureResult.Summary = [ordered]@{
                    Complete = $fixtureComplete
                    SwapChain = $fixtureSwap
                    Output = $fixtureOutput
                    FrameTimer = @($fixtureRows | Where-Object event -eq 'frame_timer')
                    CpuPresentIntervalMedianMs = if ($fixtureIntervals.Count) { $fixtureIntervals[[int][Math]::Floor($fixtureIntervals.Count / 2)] } else { $null }
                    VisibleOutputTest = $false
                    VrrMeasured = $false
                }
            }
        } else {
            $fixtureResult.Passed = $fixtureResult.Passed -and @($fixtureRows | Where-Object event -eq 'error').Count -eq 1 `
                -and @($fixtureRows | Where-Object event -eq 'window_created').Count -eq 0
        }
        if ($fixtureProcess.ExitCode -eq 0 -and $fixtureErrors.Count) { $fixtureResult.Passed = $false }
        $fixtureResults.Add([pscustomobject]$fixtureResult)
        Write-Output ('{0}: {1} (exit {2})' -f $Name, $(if ($fixtureResult.Passed) { 'PASS' } else { 'FAIL' }), $fixtureProcess.ExitCode)
    } finally {
        if (-not $fixtureProcess.HasExited) { $fixtureProcess.Kill(); [void]$fixtureProcess.WaitForExit(2000) }
        $fixtureProcess.Dispose()
    }
}

Invoke-FixtureTest 'math' @('--self-test')
Invoke-FixtureTest 'gpu-warp' @('--gpu-self-test', '--warp')
if (-not $SkipHardware) { Invoke-FixtureTest 'gpu-hardware' @('--gpu-self-test') }
Invoke-FixtureTest 'reject-unbounded-duration' @('--duration-ms', '0') @(2)
Invoke-FixtureTest 'reject-overlong-duration' @('--duration-ms', '600001') @(2)
Invoke-FixtureTest 'reject-duplicate-mode' @('--mode', 'sdr', '--mode', 'pq') @(2)
Invoke-FixtureTest 'reject-nonfinite-fps' @('--fps', 'nan') @(2)
Invoke-FixtureTest 'reject-unknown-mode' @('--mode', 'hdr') @(2)
if ($IncludeHiddenSmoke) {
    foreach ($fixtureMode in @('sdr', 'scrgb', 'pq')) {
        Invoke-FixtureTest ('hidden-' + $fixtureMode) @('--mode', $fixtureMode, '--hidden', '--no-activate',
            '--duration-ms', '500', '--fps', '59.94', '--width', '321', '--height', '181') @(0, 3)
    }
}
$fixtureSummary = [ordered]@{
    CreatedUtc = [DateTime]::UtcNow.ToString('o')
    FixturePath = $FixturePath
    FixtureSHA256 = (Get-FileHash -LiteralPath $FixturePath -Algorithm SHA256).Hash
    AllPassed = @($fixtureResults | Where-Object { -not $_.Passed }).Count -eq 0
    TestCount = $fixtureResults.Count
    Tests = @($fixtureResults.ToArray())
    AnyVisibleFixtureStarted = $false
    DisplayOutputValidated = $false
    VrrValidated = $false
    EndToEndLatencyValidated = $false
}
$fixtureSummaryPath = Join-Path $OutputDirectory 'summary.json'
$fixtureSummary | ConvertTo-Json -Depth 14 | Set-Content -LiteralPath $fixtureSummaryPath -Encoding utf8
Write-Output $fixtureSummaryPath
if (-not $fixtureSummary.AllPassed) { exit 1 }
