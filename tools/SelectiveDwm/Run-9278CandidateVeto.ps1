[CmdletBinding(DefaultParameterSetName = 'Validate')]
param(
    [Parameter(Mandatory = $true)][string]$AcceptedObserverSummary,
    [string]$OutputRoot = ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\artifacts\candidate-veto-9278'))),
    [Parameter(ParameterSetName = 'Validate')][switch]$ValidateOnly,
    [Parameter(Mandatory = $true, ParameterSetName = 'Live')][switch]$RunLive
)

# Lab acceptance only: one bounded candidate-veto session using the already
# loaded, frozen DLL after a successful SDR observer. No new DLL, gamma setting,
# independent-flip demotion, continuous mode, game input or DWM restart.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not [Environment]::Is64BitProcess) { throw 'Use 64-bit PowerShell.' }
$wrapper = Join-Path $PSScriptRoot 'Run-9278StoppedObserver.ps1'
$wrapperPin = [IO.File]::Open($wrapper, 'Open', 'Read', 'Read')
try {
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { $hash = [BitConverter]::ToString($hasher.ComputeHash($wrapperPin)).Replace('-', '') }
    finally { $hasher.Dispose() }
    if ($hash -cne 'BC619EBA31F30D299058127EB801C4CB964B05853C4A9BFBF37AA480EC693249') { throw 'Stopped-observer helper source changed.' }
    $wrapperPin.Position = 0
    $reader = [IO.StreamReader]::new($wrapperPin, [Text.Encoding]::UTF8, $true, 4096, $true)
    try { $source = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw 'Helper source did not parse.' }
    foreach ($name in @('Get-StoppedObserverDefinitions', 'Assert-StoppedObserverModules', 'Assert-StoppedObserverExclusive')) {
        $nodes = @($ast.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name }, $false))
        if ($nodes.Count -ne 1) { throw "Missing helper: $name" }
        . ([scriptblock]::Create($nodes[0].Extent.Text))
    }
} finally { $wrapperPin.Dispose() }
foreach ($definition in @(Get-StoppedObserverDefinitions (Join-Path $PSScriptRoot 'Run-9278Observer.ps1'))) {
    . ([scriptblock]::Create($definition))
}

$AcceptedObserverSummary = Get-ObserverCanonicalPath $AcceptedObserverSummary 'summary.json'
$OutputRoot = Get-ObserverCanonicalPath $OutputRoot
$locks = [Collections.Generic.List[object]]::new()
$targetHandle = $null; $dwmHandle = $null; $mutex = $null; $mutexOwned = $false; $output = $null
$report = [ordered]@{ Mode = 'ValidateOnly'; LiveStarted = $false; Accepted = $false; Error = $null;
    ObserverSummary = $AcceptedObserverSummary; ObserverSha256 = $null; Identity = $null;
    OutputDirectory = $null; Validation = $null; Controller = $null; SettingsBefore = $null; SettingsAfter = $null;
    PostIdentityRetained = $false; CrashEvents = @(); ExpiryExercised = $false; VetoedCandidates = 0;
    PinnedHookMayRemain = $true; GammaHdrVrrLatency = 'not established by candidate veto counts' }
try {
    $pin = Open-ObserverPinnedFile $AcceptedObserverSummary ''
    $locks.Add($pin); $report.ObserverSha256 = $pin.Sha256
    $prior = Get-Content -LiteralPath $AcceptedObserverSummary -Raw | ConvertFrom-Json
    Assert-ObserverCondition ($prior.Mode -ceq 'RunStoppedObserver' -and $prior.Accepted -eq $true -and
        $prior.LiveStarted -eq $true -and $null -eq $prior.Error -and $prior.PostIdentityRetained -eq $true -and
        $prior.Controller.ExitCode -eq 0 -and $prior.Controller.TimedOut -eq $false -and
        $prior.Controller.RemoteResourcesUnknown -eq $false -and $prior.SettingsBefore -ceq $prior.SettingsAfter -and
        @($prior.CrashEvents).Count -eq 0) 'A successful completed same-DLL SDR observer is required.'
    $expected = [ordered]@{}
    foreach ($property in $prior.Identity.PSObject.Properties) { $expected[$property.Name] = $property.Value }
    Assert-ObserverCondition ($expected.ExpectedControllerSha256 -ceq '9E616F0949707DE0BF394FB1AE18575C4DC6BEFBF86A636C58A2D71DC8A10A3C' -and
        $expected.ExpectedHookSha256 -ceq '45F9C9B0F3776350C953C20587447708EFA03725EFB6BE9DC16EB78E8C824B63' -and
        $expected.DwmCoreSha256 -ceq '40579ECFA394395677FEC84014C32BA5495F334B6D96C5653FD5CAA129DC1222') 'Frozen candidate identity mismatch.'
    $directory = [IO.Path]::GetDirectoryName($AcceptedObserverSummary)
    Assert-ObserverCondition ($prior.OutputDirectory -ieq $directory) 'Observer evidence directory mismatch.'
    $logPath = Join-Path $directory 'controller.stdout.jsonl'
    $locks.Add((Open-ObserverPinnedFile $logPath ''))
    $events = @(Read-ObserverEvents $logPath)
    Assert-ObserverCondition ((ConvertTo-Json -InputObject $events -Depth 16 -Compress) -ceq
        (ConvertTo-Json -InputObject @($prior.Controller.Events) -Depth 16 -Compress)) 'Observer raw log mismatch.'
    $gate = Test-9278ObserverEvidence $events $expected 0 $true $true $true 0 $false
    Assert-ObserverCondition ($gate.Accepted) 'Observer SDR/lifecycle acceptance failed.'
    $report.Identity = $expected
    $arguments = @(Get-ObserverArguments $expected)
    Assert-ObserverCondition ($arguments.Count -eq 17 -and $arguments[0] -ceq '--observe' -and $arguments[-1] -ceq '5000') 'Unexpected bounded arguments.'
    $arguments[0] = '--veto'
    $locks.Add((Open-ObserverPinnedFile $expected.ControllerPath $expected.ExpectedControllerSha256))
    $locks.Add((Open-ObserverPinnedFile $expected.HookPath $expected.ExpectedHookSha256))
    Assert-ObserverNoReparse $OutputRoot
    if (-not (Test-Path -LiteralPath $OutputRoot)) { [void](New-Item -ItemType Directory -Path $OutputRoot) }
    $output = Join-Path $OutputRoot ('veto-{0}-{1}' -f [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'), [Guid]::NewGuid().ToString('N'))
    [void](New-Item -ItemType Directory -Path $output); $report.OutputDirectory = $output
    $validation = Invoke-ObserverChild $expected.ControllerPath @($arguments + '--validate-only') $output 'validate'
    Assert-ObserverCondition (-not $validation.TimedOut) 'Local validation timed out.'
    Assert-ObserverValidation $validation.Events $expected $validation.ExitCode
    $report.Validation = $validation
    if ($RunLive) {
        $report.Mode = 'RunCandidateVeto'
        Initialize-ObserverNative
        $mutex = [Threading.Mutex]::new($false, 'Local\HDRCalibrate.First9278Observer')
        try { $mutexOwned = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $mutexOwned = $true; throw 'Previous observer owner ended abnormally.' }
        Assert-ObserverCondition $mutexOwned 'Another runner is active.'
        $targetHandle = [HdrObserver9278.Identity]::new([uint32]$expected.TargetProcessId)
        $dwmHandle = [HdrObserver9278.Identity]::new([uint32]$expected.DwmProcessId)
        $dwmExe = Join-Path ([Environment]::SystemDirectory) 'dwm.exe'
        $dwmCore = Join-Path ([Environment]::SystemDirectory) 'dwmcore.dll'
        $locks.Add((Open-ObserverPinnedFile $dwmCore $expected.DwmCoreSha256))
        $locks.Add((Open-ObserverPinnedFile $expected.TargetExe $expected.TargetExeSha256))
        Assert-ObserverIdentity $targetHandle $dwmHandle $expected $dwmExe
        Assert-StoppedObserverExclusive $expected $dwmCore
        $report.SettingsBefore = Get-ObserverSettingsSnapshot
        Assert-ObserverCondition ($report.SettingsBefore -ceq $prior.SettingsAfter) 'Settings changed since the accepted observer.'
        $started = [DateTime]::UtcNow
        Assert-ObserverIdentity $targetHandle $dwmHandle $expected $dwmExe
        Assert-StoppedObserverExclusive $expected $dwmCore
        $report.LiveStarted = $true
        $report | ConvertTo-Json -Depth 16 | Out-File -LiteralPath (Join-Path $output 'preflight.json') -Encoding utf8
        $live = Invoke-ObserverChild $expected.ControllerPath $arguments $output 'controller'
        $report.Controller = $live
        Assert-ObserverIdentity $targetHandle $dwmHandle $expected $dwmExe
        $report.PostIdentityRetained = $true
        $report.SettingsAfter = Get-ObserverSettingsSnapshot
        $report.CrashEvents = @(Get-ObserverCrashEvents $started)
        Assert-ObserverCondition ($live.ExitCode -eq 0 -and -not $live.TimedOut -and
            $report.SettingsBefore -ceq $report.SettingsAfter -and $report.CrashEvents.Count -eq 0) 'Controller/safety post-check failed.'
        $arm = Get-ObserverOneEvent $live.Events 'veto_armed'
        $start = Get-ObserverOneEvent $live.Events 'start'
        $frequency = Get-ObserverNumber $start 'qpc_frequency'
        $deadline = Get-ObserverNumber $arm 'deadline_qpc'
        Assert-ObserverCondition ($frequency -gt 0 -and $deadline - (Get-ObserverNumber $arm 'qpc') -eq $frequency * 5) 'Veto lease was not exactly five seconds.'
        $complete = Get-ObserverOneEvent $live.Events 'complete'
        foreach ($name in @('started', 'armed', 'target_identity_retained', 'dwm_identity_retained', 'stop_completed', 'no_post_expiry_veto')) {
            Assert-ObserverCondition (Get-ObserverBoolean $complete $name) "Missing completion proof: $name"
        }
        foreach ($name in @('stop_status', 'read_faults', 'active_callbacks', 'managed_error', 'game_writes')) {
            Assert-ObserverCondition ((Get-ObserverNumber $complete $name) -eq 0) "Unsafe completion: $name"
        }
        foreach ($name in @('managed', 'continuous', 'surface_rejected', 'gamma_applied', 'composition_confirmed', 'vrr_confirmed')) {
            Assert-ObserverCondition (-not (Get-ObserverBoolean $complete $name)) "Unexpected claim/action: $name"
        }
        $report.VetoedCandidates = Get-ObserverNumber $complete 'vetoed_candidates'
        Assert-ObserverCondition ($report.VetoedCandidates -gt 0) 'No target candidate was vetoed.'
        $telemetry = @($live.Events | Where-Object event -ceq 'telemetry')
        foreach ($t in $telemetry) {
            foreach ($name in @('demoted', 'read_faults', 'last_win32_error')) {
                Assert-ObserverCondition ((Get-ObserverNumber $t $name) -eq 0) "Unsafe telemetry: $name"
            }
        }
        $expired = @($telemetry | Where-Object phase -ceq 'expired')
        $recovery = @($telemetry | Where-Object phase -ceq 'recovery')
        Assert-ObserverCondition ($expired.Count -eq 1 -and $recovery.Count -eq 1) 'Missing expiry/recovery.'
        $report.ExpiryExercised = (Get-ObserverNumber $expired[0] 'expired_lease_calls') -gt 0 -and
            (Get-ObserverNumber $expired[0] 'last_callback_qpc') -ge $deadline
        Assert-ObserverCondition ($report.ExpiryExercised -and (Get-ObserverNumber $recovery[0] 'lifecycle') -eq 5 -and
            (Get-ObserverNumber $recovery[0] 'active_callbacks') -eq 0 -and
            (Get-ObserverNumber $expired[0] 'vetoed') -eq (Get-ObserverNumber $recovery[0] 'vetoed')) 'Expiry/Stop did not drain without additional vetoes.'
        $report.Accepted = $true
    }
} catch { $report.Error = $_.Exception.Message }
finally {
    if ($targetHandle) { $targetHandle.Dispose() }
    if ($dwmHandle) { $dwmHandle.Dispose() }
    if ($mutexOwned) { $mutex.ReleaseMutex() }
    if ($mutex) { $mutex.Dispose() }
    foreach ($pin in $locks) { $pin.Stream.Dispose() }
    if ($output) { $report | ConvertTo-Json -Depth 20 | Out-File -LiteralPath (Join-Path $output 'summary.json') -Encoding utf8 }
}
$report | ConvertTo-Json -Depth 20
if ($report.Error -or ($RunLive -and -not $report.Accepted)) { exit 1 }
