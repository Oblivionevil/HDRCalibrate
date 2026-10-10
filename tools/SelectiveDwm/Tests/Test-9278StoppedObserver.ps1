# Offline only: import function definitions, never either runner's body.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$path = Join-Path $PSScriptRoot '..\Run-9278StoppedObserver.ps1'
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
foreach ($name in @('Get-StoppedObserverDefinitions', 'Get-StoppedObserverPrecursor', 'Assert-StoppedObserverModules')) {
    $nodes = @($ast.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name }, $false))
    if ($nodes.Count -ne 1) { throw "Missing pure helper: $name" }
    . ([scriptblock]::Create($nodes[0].Extent.Text))
}
$definitions = @(Get-StoppedObserverDefinitions (Join-Path $PSScriptRoot '..\Run-9278Observer.ps1'))
foreach ($definition in $definitions) { . ([scriptblock]::Create($definition)) }
# Reuse only synthetic-fixture constructors from the original offline suite.
$testAst = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Test-9278Observer.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Original offline fixtures do not parse.' }
foreach ($name in @('New-Expected', 'New-Telemetry', 'New-Events')) {
    $nodes = @($testAst.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name }, $false))
    if ($nodes.Count -ne 1) { throw "Missing synthetic fixture: $name" }
    . ([scriptblock]::Create($nodes[0].Extent.Text))
}
$results = [Collections.Generic.List[object]]::new()
function Check([string]$Name, [scriptblock]$Test) { & $Test; $results.Add([ordered]@{ Name = $Name; Passed = $true }) }
function MustThrow([scriptblock]$Test) { $threw = $false; try { & $Test | Out-Null } catch { $threw = $true }; if (-not $threw) { throw 'Expected fail-closed rejection.' } }
function New-Precursor {
    $expected = New-Expected
    $expected.ControllerPath = 'C:\Owned Candidate\HDRCalibrate.SelectiveController.exe'
    $expected.ExpectedControllerSha256 = '9E616F0949707DE0BF394FB1AE18575C4DC6BEFBF86A636C58A2D71DC8A10A3C'
    $expected.ExpectedHookSha256 = '45F9C9B0F3776350C953C20587447708EFA03725EFB6BE9DC16EB78E8C824B63'
    $expected.DwmCoreSha256 = '40579ECFA394395677FEC84014C32BA5495F334B6D96C5653FD5CAA129DC1222'
    $expected.TargetExeSha256 = ('D' * 64)
    $events = New-Events
    $events[0].sha256 = $expected.ExpectedHookSha256
    $events[1].dwmcore_sha256 = $expected.DwmCoreSha256
    foreach ($t in @($events | Where-Object event -eq 'telemetry')) { $t.sample_valid = $false; $t.target_matches = 0; $t.sdr_matches = 0 }
    $gate = Test-9278ObserverEvidence $events $expected 0 $true $true $true 0 $false
    return [pscustomobject]@{ Mode = 'RunLive'; Accepted = $false; LiveStarted = $true; PinnedHookMayRemain = $true;
        Error = $null; PostIdentityRetained = $true; SettingsBefore = '[]'; SettingsAfter = '[]'; CrashEvents = @();
        Identity = [pscustomobject]$expected; Acceptance = $gate;
        Controller = [pscustomobject]@{ ProcessId = 123; ExitCode = 0; TimedOut = $false; RemoteResourcesUnknown = $false; Events = $events } }
}
Check 'clean-stopped-zero-target-precursor' {
    $s = New-Precursor; $p = Get-StoppedObserverPrecursor $s $s.Controller.Events
    if ($p.Gate.Lifecycle -ne 'passed' -or $p.Gate.Accepted -or $p.Gate.SelectedSdr -ne 'not observed') { throw 'Lifecycle proof or absent-sample distinction lost.' }
}
foreach ($pair in @(@('Mode', 'ValidateOnly'), @('Mode', 'RunStoppedObserver'), @('Error', 'failure'), @('LiveStarted', $false),
    @('PinnedHookMayRemain', $false), @('PostIdentityRetained', $false), @('SettingsAfter', '[{"changed":true}]'),
    @('SettingsBefore', $null), @('CrashEvents', @('crash')), @('Accepted', $true))) {
    $change = $pair
    Check ('precursor-reject-' + $results.Count) { $s = New-Precursor; $s.($change[0]) = $change[1]; MustThrow { Get-StoppedObserverPrecursor $s $s.Controller.Events } }
}
foreach ($pair in @(@('ExitCode', 20), @('TimedOut', $true), @('RemoteResourcesUnknown', $true), @('ProcessId', 0))) {
    $change = $pair
    Check ('controller-reject-' + $pair[0]) { $s = New-Precursor; $s.Controller.($change[0]) = $change[1]; MustThrow { Get-StoppedObserverPrecursor $s $s.Controller.Events } }
}
foreach ($name in @('ExpectedControllerSha256', 'ExpectedHookSha256', 'DwmCoreSha256', 'TargetExeSha256')) {
    $field = $name
    Check ('identity-reject-' + $name) { $s = New-Precursor; $s.Identity.$field = 'bad'; MustThrow { Get-StoppedObserverPrecursor $s $s.Controller.Events } }
}
Check 'raw-log-mismatch' {
    $s = New-Precursor; $other = ConvertFrom-Json -InputObject (ConvertTo-Json -InputObject $s.Controller.Events -Depth 16); $other[-1].read_faults = 1
    MustThrow { Get-StoppedObserverPrecursor $s $other }
}
foreach ($pair in @(@('stop_completed', $false), @('stop_status', 5), @('active_callbacks', 1), @('read_faults', 1),
    @('vetoed_candidates', 1), @('armed', $true), @('dwm_identity_retained', $false), @('target_identity_retained', $false))) {
    $change = $pair
    Check ('complete-reject-' + $pair[0]) { $s = New-Precursor; $s.Controller.Events[-1].($change[0]) = $change[1]; MustThrow { Get-StoppedObserverPrecursor $s $s.Controller.Events } }
}
Check 'stale-success-label-does-not-bypass-expiry' {
    $s = New-Precursor; foreach ($t in @($s.Controller.Events | Where-Object event -eq 'telemetry')) { $t.expired_lease_calls = 0 }
    MustThrow { Get-StoppedObserverPrecursor $s $s.Controller.Events }
}
Check 'stale-success-label-does-not-bypass-stopped-state' {
    $s = New-Precursor; $s.Controller.Events[11].lifecycle = 4
    MustThrow { Get-StoppedObserverPrecursor $s $s.Controller.Events }
}
Check 'different-target-generation' { $s = New-Precursor; $s.Identity.TargetCreationFileTime = 1L; MustThrow { Get-StoppedObserverPrecursor $s $s.Controller.Events } }
Check 'different-dwm-generation' { $s = New-Precursor; $s.Identity.DwmCreationFileTime = 1L; MustThrow { Get-StoppedObserverPrecursor $s $s.Controller.Events } }
Check 'missing-target-hash' { $s = New-Precursor; $s.Identity.PSObject.Properties.Remove('TargetExeSha256'); MustThrow { Get-StoppedObserverPrecursor $s $s.Controller.Events } }
$core = 'C:\Windows\System32\dwmcore.dll'
$e = (Get-StoppedObserverPrecursor (New-Precursor) (New-Precursor).Controller.Events).Expected
Check 'exact-existing-module' { Assert-StoppedObserverModules @($core, $e.HookPath) $e $core 0 }
Check 'no-hook-no-fallback' { MustThrow { Assert-StoppedObserverModules @($core) $e $core 0 } }
Check 'foreign-hook-no-replacement' { MustThrow { Assert-StoppedObserverModules @($core, 'C:\Other\HDRCalibrate.SelectiveDwmHook.dll') $e $core 0 } }
Check 'multiple-hooks-refused' { MustThrow { Assert-StoppedObserverModules @($core, $e.HookPath, 'C:\Other\HDRCalibrate.SelectiveDwmHook.dll') $e $core 0 } }
Check 'renamed-additional-hook-refused' { MustThrow { Assert-StoppedObserverModules @($core, $e.HookPath, 'C:\Other\OldSelectiveDwmHook.dll') $e $core 0 } }
Check 'controller-already-active' { MustThrow { Assert-StoppedObserverModules @($core, $e.HookPath) $e $core 1 } }
Check 'wrong-core-module' { MustThrow { Assert-StoppedObserverModules @('C:\Other\dwmcore.dll', $e.HookPath) $e $core 0 } }
Check 'duplicate-core-module' { MustThrow { Assert-StoppedObserverModules @($core, $core, $e.HookPath) $e $core 0 } }
Check 'definition-loader-is-fail-closed' { MustThrow { Get-StoppedObserverDefinitions $path } }
Check 'pinned-import-count' { if ($definitions.Count -ne 18) { throw 'Unexpected import set.' } }
Check 'no-live-helpers-called-outside-opt-in' {
    $calls = @($ast.FindAll({ param($n) $n -is [Management.Automation.Language.CommandAst] -and $n.GetCommandName() -ceq 'Invoke-ObserverChild' }, $true))
    if ($calls.Count -ne 2) { throw 'Expected one validation and one explicit live call.' }
    $live = @($calls | Where-Object { $_.Extent.Text -notmatch '\-Validation' })
    if ($live.Count -ne 1) { throw 'Ambiguous live call.' }
    $node = $live[0].Parent; $guarded = $false
    while ($node) {
        if ($node -is [Management.Automation.Language.IfStatementAst] -and $node.Clauses[0].Item1.Extent.Text -ceq '$RunLive') { $guarded = $true }
        $node = $node.Parent
    }
    if (-not $guarded) { throw 'Explicit live opt-in missing.' }
    if ($ast.Extent.Text -notmatch "DefaultParameterSetName = 'Validate'" -or $ast.Extent.Text -match '--veto|--continuous|FreeLibrary|Restart-Computer') { throw 'Unsafe wrapper mode/action.' }
}
[ordered]@{ Passed = $results.Count; Cases = @($results.ToArray()); LiveActivation = $false; ProcessAccess = $false;
    NativeApisInitialized = $false; ControllerInvoked = $false } | ConvertTo-Json -Depth 5
