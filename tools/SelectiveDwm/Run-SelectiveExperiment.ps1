param(
    [Parameter(Mandatory = $true)][int]$TargetProcessId,
    [Parameter(Mandatory = $true)][ulong]$TargetWindow,
    [ValidateSet('observe', 'veto')][string]$Mode = 'observe',
    [switch]$WithVrrPolling,
    [string]$BinaryDirectory,
    [switch]$ValidateOnly
)

# Explicit, bounded experimental DWM test. No game launch, input/focus,
# application-memory writes, display/registry changes, or DWM restart.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (-not $BinaryDirectory) { $BinaryDirectory = Join-Path $taskRoot 'artifacts\selective-dwm-20260906\v3\bin' }
$controllerPath = (Resolve-Path -LiteralPath (Join-Path $BinaryDirectory 'HDRCalibrate.SelectiveController.exe')).Path
$hookPath = (Resolve-Path -LiteralPath (Join-Path $BinaryDirectory 'HDRCalibrate.SelectiveDwmHook.dll')).Path
$hookHash = (Get-FileHash -LiteralPath $hookPath -Algorithm SHA256).Hash
if ($hookHash -ne '128556F6904418F928D72875B974674FC0A0BF1C90209570D7E6D0D1AB6B0596') {
    throw 'This live experiment accepts only the reviewed, frozen v3 hook image.'
}
$targetProcess = Get-Process -Id $TargetProcessId -ErrorAction Stop
if ($targetProcess.ProcessName -notin @('HDRCalibrate.ColorFixture', 'HeroesOfTheStorm_x64')) {
    throw 'Only the already-running own fixture or HotS is allowed by this test runner.'
}
if (-not $targetProcess.Responding -or -not $TargetWindow) { throw 'Invalid or unresponsive target.' }
$targetStart = $targetProcess.StartTime.ToUniversalTime().Ticks
$monitorPath = (Get-Command presentmon.exe -ErrorAction Stop).Source
$vrrPath = Join-Path $taskRoot 'artifacts\heroes-sdr-feasibility\Probe-VrrStatus.exe'
if ($WithVrrPolling) { $vrrPath = (Resolve-Path -LiteralPath $vrrPath).Path }
$dwmBefore = @(Get-Process -Name dwm | Select-Object Id, SessionId, StartTime)
if ($ValidateOnly) {
    [ordered]@{ Mode = $Mode; TargetPid = $TargetProcessId; TargetWindow = $TargetWindow; TargetStartUtcTicks = $targetStart;
        Controller = $controllerPath; Hook = $hookPath; HookSha256 = $hookHash; Dwm = $dwmBefore } | ConvertTo-Json -Depth 5
    exit 0
}
$testToken = [Guid]::NewGuid().ToString('N')
$outputDirectory = Join-Path $taskRoot ('artifacts\selective-dwm-20260906\experiment-{0}-{1}-{2}' -f
    (Get-Date -Format 'yyyyMMdd-HHmmss'), $Mode, $testToken.Substring(0, 8))
New-Item -ItemType Directory -Path $outputDirectory | Out-Null
$ownedProcesses = [Collections.Generic.List[Diagnostics.Process]]::new()
function Start-TestHelper([string]$Path, [string[]]$Arguments, [string]$Label) {
    $nativeArguments = @($Arguments | ForEach-Object {
        if ($_ -match '["\r\n]') { throw 'Invalid native argument.' }
        '"' + $_ + '"'
    }) -join ' '
    $child = Start-Process -FilePath $Path -ArgumentList $nativeArguments -WindowStyle Hidden -PassThru -WorkingDirectory $taskRoot `
        -RedirectStandardOutput (Join-Path $outputDirectory "$Label.stdout.log") `
        -RedirectStandardError (Join-Path $outputDirectory "$Label.stderr.log")
    $ownedProcesses.Add($child)
    return $child
}
function Wait-TestHelper([Diagnostics.Process]$Child, [int]$TimeoutMs) {
    if (-not $Child.WaitForExit($TimeoutMs)) { throw "Owned test helper timed out: $($Child.Id)." }
    $Child.Refresh()
}
$report = [ordered]@{
    StartedUtc = [DateTime]::UtcNow.ToString('o'); Mode = $Mode; TargetPid = $TargetProcessId; TargetWindow = $TargetWindow;
    TargetStartUtcTicks = $targetStart; DwmBefore = $dwmBefore; HookSha256 = $hookHash; VrrPolling = [bool]$WithVrrPolling;
    ControllerExitCode = $null; PresentMonExitCode = $null; VrrExitCode = $null; TargetIdentityRetained = $false;
    DwmAfter = @(); ControllerComplete = $null; TraceSummary = $null; VrrPhases = @(); Error = $null;
    GammaApplied = $false; OpticalColorMeasurement = $false; InputToPhotonMeasured = $false
}
Write-Output "Test artifacts: $outputDirectory"
try {
    $tracePath = Join-Path $outputDirectory 'presents.csv'
    $monitor = Start-TestHelper $monitorPath @('--process_id', [string]$TargetProcessId,
        '--session_name', ('HDRCalibrateSelective-' + $testToken), '--timed', '25', '--terminate_after_timed',
        '--no_console_stats', '--no_track_input', '--v1_metrics', '--qpc_time', '--output_file', $tracePath) 'presentmon'
    $vrr = $null
    if ($WithVrrPolling) {
        $vrr = Start-TestHelper $vrrPath @('--duration-ms', '25000', '--interval-ms', '100', '--vblank') 'vrr'
    }
    Start-Sleep -Seconds 5
    $controller = Start-TestHelper $controllerPath @("--$Mode", '--dll', $hookPath, '--target-pid', [string]$TargetProcessId,
        '--target-hwnd', [string]$TargetWindow, '--duration-ms', '5000') 'controller'
    Wait-TestHelper $controller 20000
    $report.ControllerExitCode = $controller.ExitCode
    Wait-TestHelper $monitor 30000
    $report.PresentMonExitCode = $monitor.ExitCode
    if ($null -ne $vrr) { Wait-TestHelper $vrr 5000; $report.VrrExitCode = $vrr.ExitCode }
    $events = @(Get-Content -LiteralPath (Join-Path $outputDirectory 'controller.stdout.log') |
        Where-Object { $_ -match '^\{' } | ForEach-Object { $_ | ConvertFrom-Json })
    $start = @($events | Where-Object event -eq 'start')
    $arm = @($events | Where-Object event -eq 'veto_armed')
    $complete = @($events | Where-Object event -eq 'complete')
    if ($complete.Count -eq 1) { $report.ControllerComplete = $complete[0] }
    if ($start.Count -ne 1 -or -not $start[0].completed -or $start[0].status -ne 0 -or $report.PresentMonExitCode -ne 0) {
        throw 'Controller start or ETW capture failed; inspect raw logs.'
    }
    $begin = [long]$start[0].issued_qpc; $end = [long]$start[0].deadline_qpc
    if ($arm.Count -eq 1) { $begin = [long]$arm[0].qpc; $end = [long]$arm[0].deadline_qpc }
    $analysisJson = & (Join-Path $taskRoot 'tools\CompositionProbe\Analyze-CompositionTrace.ps1') -TracePath $tracePath `
        -ShownQpc $begin -HiddenQpc $end -QpcFrequency ([long]$start[0].qpc_frequency) -TargetProcessId $TargetProcessId
    $report.TraceSummary = $analysisJson | ConvertFrom-Json
    $report.TraceSummary.Phases[1].Name = if ($Mode -eq 'veto' -and $arm.Count -eq 1) { 'selected-veto' } else { 'observe-only' }
    if ($WithVrrPolling) {
        $vrrRows = @(Import-Csv -LiteralPath (Join-Path $outputDirectory 'vrr.stdout.log'))
        foreach ($phase in $report.TraceSummary.Phases) {
            $phaseSamples = @($vrrRows | Where-Object { [long]$_.QpcBefore -ge $phase.StartQpc -and [long]$_.QpcAfter -lt $phase.EndQpc })
            $report.VrrPhases += [ordered]@{ Name = $phase.Name; Samples = $phaseSamples.Count;
                States = @($phaseSamples | Group-Object DisplayId, GetStatus, RawFlags, IsVRREnabled, IsVRRPossible, IsVRRRequested, IsDisplayInVRRMode, ForegroundPid |
                    ForEach-Object { [ordered]@{ State = $_.Name; Count = $_.Count } }) }
        }
    }
} catch {
    $report.Error = $_.Exception.Message
} finally {
    foreach ($child in $ownedProcesses) {
        # Only exact process objects created by this invocation. Never look up or
        # stop a game, DWM, existing monitor, or the user's installed application.
        if (-not $child.HasExited) { $child.Kill(); [void]$child.WaitForExit(3000) }
    }
    $targetAfter = Get-Process -Id $TargetProcessId -ErrorAction SilentlyContinue
    $report.TargetIdentityRetained = $null -ne $targetAfter -and $targetAfter.StartTime.ToUniversalTime().Ticks -eq $targetStart
    $report.DwmAfter = @(Get-Process -Name dwm | Select-Object Id, SessionId, StartTime)
    $report | ConvertTo-Json -Depth 12 | Out-File -LiteralPath (Join-Path $outputDirectory 'summary.json') -Encoding utf8
}
$report | ConvertTo-Json -Depth 12
if ($report.Error) { exit 1 }
if ($report.ControllerExitCode -ne 0) { exit $report.ControllerExitCode }
