param(
    [Parameter(Mandatory = $true)][int]$TargetProcessId,
    [ValidateSet('clear', 'faint', 'marker', 'alpha', 'multi')][string]$Mode = 'marker',
    [ValidateRange(5, 15)][int]$DelaySeconds = 15,
    [string]$ProbePath,
    [string]$VrrProbePath,
    [switch]$VBlank,
    [switch]$WithoutVrrPolling,
    [switch]$ValidateOnly
)

# Experimental test orchestration only. No game launch, focus operation, input,
# game/process-memory writes, display settings, registry writes or DWM patch.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$research = Join-Path $root 'artifacts\heroes-sdr-feasibility'
if (-not $ProbePath) { $ProbePath = Join-Path $research 'composition-20260906\HDRCalibrate.CompositionProbe.exe' }
if (-not $VrrProbePath) { $VrrProbePath = Join-Path $research 'Probe-VrrStatus.exe' }
$ProbePath = (Resolve-Path -LiteralPath $ProbePath).Path
if (-not $WithoutVrrPolling) { $VrrProbePath = (Resolve-Path -LiteralPath $VrrProbePath).Path }
$presentMon = (Get-Command presentmon.exe -ErrorAction Stop).Source
$target = Get-Process -Id $TargetProcessId -ErrorAction Stop
if ($target.ProcessName -ne 'HeroesOfTheStorm_x64') { throw 'The experiment only accepts the already-running HotS process.' }
$targetIdentity = [ordered]@{
    Id = $target.Id
    Path = $target.Path
    StartTimeUtcTicks = $target.StartTime.ToUniversalTime().Ticks
    Responding = $target.Responding
}
if (-not $targetIdentity.Responding) { throw 'HotS is not responding; no test will start.' }
$dwmBefore = @(Get-Process -Name dwm | Select-Object Id, SessionId, StartTime)
$probes = @($ProbePath, $presentMon)
if (-not $WithoutVrrPolling) { $probes += $VrrProbePath }
$hashes = @($probes | ForEach-Object { Get-FileHash -LiteralPath $_ -Algorithm SHA256 | Select-Object Path, Hash })
if ($ValidateOnly) {
    [ordered]@{ Target = $targetIdentity; Mode = $Mode; DelaySeconds = $DelaySeconds; Files = $hashes } | ConvertTo-Json -Depth 6
    exit 0
}

$token = [Guid]::NewGuid().ToString('N')
$output = Join-Path $research (('composition-{0}-{1}-{2}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $Mode, $token.Substring(0, 8)))
New-Item -ItemType Directory -Path $output | Out-Null
$captureSeconds = $DelaySeconds + 12
$pm = $null
$overlay = $null
$vrr = $null
$report = [ordered]@{
    StartedUtc = [DateTime]::UtcNow.ToString('o')
    Mode = $Mode
    TargetBefore = $targetIdentity
    DwmBefore = $dwmBefore
    Files = $hashes
    GammaChanged = $false
    VrrPolling = -not $WithoutVrrPolling
    VBlankRequested = [bool]$VBlank
    OverlayExitCode = $null
    PresentMonExitCode = $null
    VrrExitCode = $null
    VisibleIntervalMs = $null
    TestCompleted = $false
    Error = $null
    TargetAfter = $null
    DwmAfter = $null
    TraceSummary = $null
    VrrPhases = @()
}

function Start-OwnedHelper {
    param([string]$Path, [string[]]$Arguments, [string]$Label)
    # File paths are generated here and quoted for native argv, never shell text.
    $quoted = @($Arguments | ForEach-Object {
        if ($_ -match '["\r\n]') { throw 'Invalid native argument.' }
        '"' + $_ + '"'
    }) -join ' '
    Start-Process -FilePath $Path -ArgumentList $quoted -WorkingDirectory $root -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $output "$Label.stdout.log") `
        -RedirectStandardError (Join-Path $output "$Label.stderr.log")
}

function Finish-OwnedHelper {
    param([Diagnostics.Process]$Process, [int]$TimeoutMs)
    if ($null -eq $Process) { return }
    if (-not $Process.WaitForExit($TimeoutMs)) {
        # This object comes directly from Start-Process in this invocation.
        # Never look up or stop a game, DWM or existing monitoring process.
        $Process.Kill()
        if (-not $Process.WaitForExit(3000)) { throw 'An owned test helper failed to exit.' }
        throw 'An owned test helper exceeded its bounded duration and was stopped.'
    }
    $Process.Refresh()
}

try {
    $csv = Join-Path $output 'presents.csv'
    $pm = Start-OwnedHelper $presentMon @(
        '--process_id', [string]$TargetProcessId,
        '--session_name', ('HDRCalibrateComposition-' + $token),
        '--timed', [string]$captureSeconds, '--terminate_after_timed',
        '--no_console_stats', '--no_track_input', '--v1_metrics', '--qpc_time',
        '--output_file', $csv
    ) 'presentmon'
    if (-not $WithoutVrrPolling) {
        $vrrArgs = @('--duration-ms', [string]($captureSeconds * 1000), '--interval-ms', '100')
        if ($VBlank) { $vrrArgs += '--vblank' }
        $vrr = Start-OwnedHelper $VrrProbePath $vrrArgs 'vrr'
    }
    $overlay = Start-OwnedHelper $ProbePath @(
        '--pid', [string]$TargetProcessId, '--mode', $Mode,
        '--duration-ms', '5000', '--delay-ms', [string]($DelaySeconds * 1000)
    ) 'overlay'
    Write-Output "Test artifacts: $output"
    Finish-OwnedHelper $overlay (($DelaySeconds + 8) * 1000)
    $report.OverlayExitCode = $overlay.ExitCode
    Finish-OwnedHelper $pm 15000
    $report.PresentMonExitCode = $pm.ExitCode
    if ($null -ne $vrr) {
        Finish-OwnedHelper $vrr 4000
        $report.VrrExitCode = $vrr.ExitCode
    }
    $events = @(Get-Content -LiteralPath (Join-Path $output 'overlay.stdout.log') | Where-Object { $_ -match '^\{' } | ForEach-Object { $_ | ConvertFrom-Json })
    $shown = @($events | Where-Object event -eq 'shown')
    $hidden = @($events | Where-Object event -eq 'hidden')
    $config = @($events | Where-Object event -eq 'config')
    if ($report.OverlayExitCode -ne 0 -or $shown.Count -ne 1 -or $hidden.Count -ne 1 -or $config.Count -ne 1) {
        throw 'Overlay did not complete one normal visible interval; inspect raw logs. No success is inferred.'
    }
    if ($report.PresentMonExitCode -ne 0) { throw 'PresentMon capture failed.' }
    $frequency = [long]$config[0].qpc_frequency
    $shownTime = [long]$shown[0].qpc_ticks
    $hiddenTime = [long]$hidden[0].hide_complete_qpc_ticks
    $report.VisibleIntervalMs = ($hiddenTime - $shownTime) * 1000.0 / $frequency
    $analysisPath = Join-Path $output 'trace-summary.json'
    & (Join-Path $PSScriptRoot 'Analyze-CompositionTrace.ps1') -TracePath $csv -ShownQpc $shownTime `
        -HiddenQpc $hiddenTime -QpcFrequency $frequency -TargetProcessId $TargetProcessId -OutputPath $analysisPath | Out-Null
    $report.TraceSummary = Get-Content -LiteralPath $analysisPath -Raw | ConvertFrom-Json
    if ($null -ne $vrr) {
        $vrrRows = @(Import-Csv -LiteralPath (Join-Path $output 'vrr.stdout.log'))
        foreach ($phase in $report.TraceSummary.Phases) {
            $samples = @($vrrRows | Where-Object {
                [long]$_.QpcBefore -ge $phase.StartQpc -and [long]$_.QpcAfter -lt $phase.EndQpc
            })
            $states = @($samples | Group-Object DisplayId, GetStatus, IsVRREnabled, IsVRRRequested, IsDisplayInVRRMode, ForegroundPid | ForEach-Object {
                [ordered]@{ State = $_.Name; Count = $_.Count }
            })
            $counterRates = @()
            if ($VBlank) {
                foreach ($displaySamples in @($samples | Group-Object DisplayId)) {
                    $valid = @($displaySamples.Group | Where-Object VBlankStatus -eq '0' | Sort-Object { [long]$_.VBlankBeforeQpc })
                    if ($valid.Count -lt 2) { continue }
                    $first = $valid[0]
                    $last = $valid[-1]
                    $firstMid = ([double]$first.VBlankBeforeQpc + [double]$first.VBlankAfterQpc) / 2
                    $lastMid = ([double]$last.VBlankBeforeQpc + [double]$last.VBlankAfterQpc) / 2
                    $seconds = ($lastMid - $firstMid) / [double]$first.QpcFrequency
                    $delta = ([long]$last.VBlankCounter - [long]$first.VBlankCounter + 4294967296L) % 4294967296L
                    if ($seconds -lt 1) { continue }
                    $counterRates += [ordered]@{
                        DisplayId = $displaySamples.Name
                        Samples = $valid.Count
                        DurationSeconds = [Math]::Round($seconds, 6)
                        CounterDelta = $delta
                        DriverVBlankRateHz = [Math]::Round($delta / $seconds, 3)
                        Caveat = 'Driver counter rate; not an optical scanout measurement. LFC and repeated frames may raise it above game FPS.'
                    }
                }
            }
            $report.VrrPhases += [ordered]@{
                Name = $phase.Name
                Samples = $samples.Count
                StateColumnOrder = 'DisplayId, GetStatus, IsVRREnabled, IsVRRRequested, IsDisplayInVRRMode, ForegroundPid'
                States = $states
                ForegroundTargetSamples = @($samples | Where-Object { [int]$_.ForegroundPid -eq $TargetProcessId }).Count
                GetterFailedSamples = @($samples | Where-Object GetStatus -ne '0').Count
                VBlankRates = $counterRates
            }
        }
    }
    $report.TestCompleted = $true
} catch {
    $report.Error = $_.Exception.Message
} finally {
    # Every process here was created by this invocation. In normal operation
    # they self-expire; on orchestration failure only those exact helpers stop.
    foreach ($owned in @($overlay, $vrr, $pm)) {
        if ($null -ne $owned) {
            try {
                if (-not $owned.HasExited) { $owned.Kill(); [void]$owned.WaitForExit(3000) }
            } catch { $report.Error = [string]$report.Error + ' Helper cleanup: ' + $_.Exception.Message }
        }
    }
    try {
        $after = Get-Process -Id $TargetProcessId -ErrorAction Stop
        $report.TargetAfter = [ordered]@{
            Id = $after.Id; StartTimeUtcTicks = $after.StartTime.ToUniversalTime().Ticks; Responding = $after.Responding
        }
        if ($report.TargetAfter.StartTimeUtcTicks -ne $targetIdentity.StartTimeUtcTicks) {
            $report.Error = [string]$report.Error + ' Target process identity changed.'
            $report.TestCompleted = $false
        }
    } catch { $report.Error = [string]$report.Error + ' Target status: ' + $_.Exception.Message; $report.TestCompleted = $false }
    $report.DwmAfter = @(Get-Process -Name dwm | Select-Object Id, SessionId, StartTime)
    $report.EndedUtc = [DateTime]::UtcNow.ToString('o')
    [IO.File]::WriteAllText((Join-Path $output 'experiment.json'), ($report | ConvertTo-Json -Depth 14), [Text.UTF8Encoding]::new($false))
}
$report | ConvertTo-Json -Depth 14
if (-not $report.TestCompleted) { exit 1 }
