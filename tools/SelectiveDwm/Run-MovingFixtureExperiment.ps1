param(
    [ValidateSet(120, 137)][int]$FrameRate = 120,
    [ValidateSet('observe', 'veto')][string]$Mode = 'observe',
    [switch]$WithVrrPolling,
    [switch]$ValidateOnly
)

function Assert-MovingCaptureResult([System.Collections.IDictionary]$Result) {
    foreach ($field in @('ControllerExitCode', 'PresentMonExitCode', 'FixtureExitCode')) {
        if ($null -eq $Result[$field] -or $Result[$field] -ne 0) { throw "Required helper failed or did not finish: $field." }
    }
    if ($Result['VrrPolling'] -and ($null -eq $Result['VrrExitCode'] -or $Result['VrrExitCode'] -ne 0)) {
        throw 'Requested VRR helper failed or did not finish.'
    }
    if (-not $Result['TargetIdentityRetained']) { throw 'Fixture identity was not retained through capture.' }
    if (-not $Result['DwmIdentityRetained']) { throw 'DWM identity changed during the experiment.' }
}

# Lab-only live test. ETW starts before our own moving-pattern process exists.
# No game launch/termination, foreign application writes, display changes or DWM restart.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$artifactRoot = Join-Path $taskRoot 'artifacts\selective-dwm-20260906'
$fixturePath = (Resolve-Path -LiteralPath (Join-Path $artifactRoot 'fixture-moving\bin\HDRCalibrate.ColorFixture.exe')).Path
$hookPath = (Resolve-Path -LiteralPath (Join-Path $artifactRoot 'v3\bin\HDRCalibrate.SelectiveDwmHook.dll')).Path
$controllerPath = (Resolve-Path -LiteralPath (Join-Path $artifactRoot 'v3\bin\HDRCalibrate.SelectiveController.exe')).Path
$monitorPath = (Get-Command presentmon.exe -ErrorAction Stop).Source
$vrrPath = Join-Path $taskRoot 'artifacts\heroes-sdr-feasibility\Probe-VrrStatus.exe'
$hookHash = (Get-FileHash -LiteralPath $hookPath -Algorithm SHA256).Hash
$fixtureHash = (Get-FileHash -LiteralPath $fixturePath -Algorithm SHA256).Hash
if ($hookHash -ne '128556F6904418F928D72875B974674FC0A0BF1C90209570D7E6D0D1AB6B0596' -or
    $fixtureHash -ne '238DA49635DAE0CDEC584B2C69C4AA84E9C189AEFBCD77358C8A7E903309DB95') {
    throw 'The lab runner requires the reviewed frozen v3 hook and moving fixture.'
}
if (@(Get-Process -Name 'HDRCalibrate.ColorFixture' -ErrorAction SilentlyContinue).Count) {
    throw 'An existing fixture is running; do not mix its trace with this test.'
}
if ($WithVrrPolling) { $vrrPath = (Resolve-Path -LiteralPath $vrrPath).Path }
if ($ValidateOnly) {
    [ordered]@{ Mode = $Mode; FrameRate = $FrameRate; HookSha256 = $hookHash;
        FixtureSha256 = $fixtureHash; EtwBeforeProcessCreation = $true;
        LiveProcessesStarted = $false; PlannedFixtureMayActivate = $true } | ConvertTo-Json
    exit 0
}
$token = [Guid]::NewGuid().ToString('N')
$outputDirectory = Join-Path $artifactRoot ('moving-{0}-{1}-{2}-{3}' -f
    (Get-Date -Format 'yyyyMMdd-HHmmss'), $FrameRate, $Mode, $token.Substring(0, 8))
New-Item -ItemType Directory -Path $outputDirectory | Out-Null
$ownedProcesses = [Collections.Generic.List[Diagnostics.Process]]::new()
function Start-OwnedHelper([string]$Path, [string[]]$Arguments, [string]$Label) {
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
function Wait-OwnedHelper([Diagnostics.Process]$Child, [int]$TimeoutMs) {
    if (-not $Child.WaitForExit($TimeoutMs)) { throw "Owned helper timed out: $($Child.Id)." }
    $Child.Refresh()
}
$report = [ordered]@{
    StartedUtc = [DateTime]::UtcNow.ToString('o'); Mode = $Mode; FrameRate = $FrameRate;
    HookSha256 = $hookHash; FixtureSha256 = $fixtureHash; EtwBeforeProcessCreation = $true;
    TargetPid = 0; TargetWindow = 0; TargetStartUtcTicks = 0;
    DwmBefore = @(Get-Process -Name dwm | Select-Object Id, SessionId, StartTime); DwmAfter = @();
    VrrPolling = [bool]$WithVrrPolling; ControllerExitCode = $null; PresentMonExitCode = $null;
    FixtureExitCode = $null; VrrExitCode = $null; ControllerComplete = $null; TraceSummary = $null;
    TargetIdentityRetained = $false; DwmIdentityRetained = $false; Error = $null; GammaApplied = $false;
    OpticalColorMeasurement = $false; InputToPhotonMeasured = $false
}
Write-Output "Test artifacts: $outputDirectory"
try {
    $tracePath = Join-Path $outputDirectory 'presents.csv'
    $monitor = Start-OwnedHelper $monitorPath @('--process_name', 'HDRCalibrate.ColorFixture.exe',
        '--session_name', ('HDRCalibrateMoving-' + $token), '--timed', '30', '--terminate_after_timed',
        '--no_console_stats', '--no_track_input', '--track_etw_status', '--v1_metrics', '--qpc_time',
        '--output_file', $tracePath) 'presentmon'
    Start-Sleep -Milliseconds 750
    if ($monitor.HasExited) { throw 'PresentMon exited before fixture creation.' }
    $fixture = Start-OwnedHelper $fixturePath @('--mode', 'sdr', '--duration-ms', '33000', '--fps', [string]$FrameRate,
        '--width', '3840', '--height', '2160', '--x', '0', '--y', '0', '--borderless', '--animate-marker') 'fixture'
    $report.TargetPid = $fixture.Id
    $report.TargetStartUtcTicks = $fixture.StartTime.ToUniversalTime().Ticks
    $ready = @()
    $readyDeadline = [DateTime]::UtcNow.AddSeconds(4)
    do {
        Start-Sleep -Milliseconds 100
        $ready = @(Get-Content -LiteralPath (Join-Path $outputDirectory 'fixture.stdout.log') |
            Where-Object { $_ -match '^\{"event":"ready"' } | ForEach-Object { $_ | ConvertFrom-Json })
    } while ($ready.Count -eq 0 -and -not $fixture.HasExited -and [DateTime]::UtcNow -lt $readyDeadline)
    if ($ready.Count -ne 1 -or $ready[0].pid -ne $fixture.Id -or -not $ready[0].visible) { throw 'Own fixture not ready.' }
    $report.TargetWindow = [ulong]$ready[0].hwnd
    $vrr = $null
    if ($WithVrrPolling) { $vrr = Start-OwnedHelper $vrrPath @('--duration-ms', '28000', '--interval-ms', '100', '--vblank') 'vrr' }
    Start-Sleep -Seconds 5
    $controller = Start-OwnedHelper $controllerPath @("--$Mode", '--dll', $hookPath, '--target-pid', [string]$fixture.Id,
        '--target-hwnd', [string]$report.TargetWindow, '--duration-ms', '5000') 'controller'
    Wait-OwnedHelper $controller 20000
    $report.ControllerExitCode = $controller.ExitCode
    Wait-OwnedHelper $monitor 30000
    $report.PresentMonExitCode = $monitor.ExitCode
    if ($null -ne $vrr) { Wait-OwnedHelper $vrr 5000; $report.VrrExitCode = $vrr.ExitCode }
    $fixture.Refresh()
    $report.TargetIdentityRetained = -not $fixture.HasExited -and $fixture.StartTime.ToUniversalTime().Ticks -eq $report.TargetStartUtcTicks
    Wait-OwnedHelper $fixture 10000
    $report.FixtureExitCode = $fixture.ExitCode
    if ($report.FixtureExitCode -ne 0 -or ($WithVrrPolling -and $report.VrrExitCode -ne 0)) {
        throw 'Fixture or requested VRR helper failed; the experiment is not accepted.'
    }
    if (-not $report.TargetIdentityRetained) { throw 'Fixture identity was not retained through capture.' }
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
    $report.TraceSummary = (& (Join-Path $taskRoot 'tools\CompositionProbe\Analyze-CompositionTrace.ps1') -TracePath $tracePath `
        -ShownQpc $begin -HiddenQpc $end -QpcFrequency ([long]$start[0].qpc_frequency) -TargetProcessId $fixture.Id) | ConvertFrom-Json
    $report.TraceSummary.Phases[1].Name = if ($Mode -eq 'veto' -and $arm.Count -eq 1) { 'selected-veto' } else { 'observe-only' }
} catch {
    $report.Error = $_.Exception.Message
} finally {
    foreach ($child in $ownedProcesses) {
        # Exact process objects owned by this run, never game/DWM/existing ETW.
        if (-not $child.HasExited) { $child.Kill(); [void]$child.WaitForExit(3000) }
    }
    $report.DwmAfter = @(Get-Process -Name dwm | Select-Object Id, SessionId, StartTime)
    $beforeIdentity = @($report.DwmBefore | ForEach-Object { '{0}/{1}/{2}' -f $_.Id, $_.SessionId, $_.StartTime.ToUniversalTime().Ticks } | Sort-Object) -join ';'
    $afterIdentity = @($report.DwmAfter | ForEach-Object { '{0}/{1}/{2}' -f $_.Id, $_.SessionId, $_.StartTime.ToUniversalTime().Ticks } | Sort-Object) -join ';'
    $report.DwmIdentityRetained = $beforeIdentity.Length -gt 0 -and $beforeIdentity -ceq $afterIdentity
    if (-not $report.Error) {
        try { Assert-MovingCaptureResult $report } catch { $report.Error = $_.Exception.Message }
    }
    $report | ConvertTo-Json -Depth 12 | Out-File -LiteralPath (Join-Path $outputDirectory 'summary.json') -Encoding utf8
}
$report | ConvertTo-Json -Depth 12
if ($report.Error) { exit 1 }
if ($report.ControllerExitCode -ne 0) { exit $report.ControllerExitCode }
