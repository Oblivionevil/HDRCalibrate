# Offline only: extract an explicit allowlist of pure/local-file functions by
# AST. NEVER dot-source the runner body, initialize native process APIs, invoke
# a controller, open DWM/HotS, or create a trace session.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$runnerPath = Join-Path $PSScriptRoot '..\Run-9278Observer.ps1'
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($runnerPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
$allowed = @('Get-ObserverCanonicalPath', 'Assert-ObserverNoReparse', 'Get-ObserverField', 'Get-ObserverNumber',
    'Get-ObserverBoolean', 'Assert-ObserverCondition', 'Get-ObserverOneEvent', 'Get-ObserverArguments',
    'ConvertTo-ObserverNativeArguments', 'Assert-ObserverValidation', 'Test-9278ObserverEvidence', 'Open-ObserverPinnedFile')
foreach ($name in $allowed) {
    $functions = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name }, $false))
    if ($functions.Count -ne 1) { throw "Expected one allowlisted function: $name" }
    . ([scriptblock]::Create($functions[0].Extent.Text))
}
$results = [Collections.Generic.List[object]]::new()
function Check([string]$Name, [scriptblock]$Test) {
    & $Test
    $results.Add([ordered]@{ Name = $Name; Passed = $true })
}
function MustThrow([scriptblock]$Test) {
    $threw = $false
    try { & $Test | Out-Null } catch { $threw = $true }
    if (-not $threw) { throw 'Expected a fail-closed rejection.' }
}
function New-Expected {
    return [ordered]@{ HookPath = 'C:\Owned Candidate\HDRCalibrate.SelectiveDwmHook.dll'; ExpectedHookSha256 = ('A' * 64);
        ExpectedControllerSha256 = ('B' * 64); TargetProcessId = 100; TargetWindow = 200L;
        TargetExe = 'F:\Heroes\HeroesOfTheStorm_x64.exe'; TargetCreationFileTime = 134330000000000000L;
        DwmProcessId = 101; DwmCreationFileTime = 134329000000000000L; DwmCoreSha256 = ('C' * 64) }
}
function New-Telemetry([string]$Phase, [long]$Qpc, [long]$SampleQpc, [long]$Calls, [long]$Expired, [int]$Lifecycle) {
    return [pscustomobject]@{ event = 'telemetry'; phase = $Phase; qpc = $Qpc; lifecycle = $Lifecycle; capabilities = 7;
        candidate_calls = $Calls; iflip_calls = $Calls; target_matches = $Calls; sdr_matches = $Calls;
        vetoed = 0; demoted = 0; read_faults = 0; active_callbacks = 0; expired_lease_calls = $Expired;
        sample_valid = $true; sample_qpc = $SampleQpc; sample_stage = 7; point = 1; decision = 0;
        visual = 300L; surface = 400L; bind = 4L; hwnd = 200L; owner_pid = 100; colorspace = 0; format = 87; flags = 60;
        latest_valid = $true; latest_stage = 7; latest_decision = $(if ($Expired) { 7 } else { 0 });
        last_callback_qpc = ($Qpc - 1); last_win32_error = 0 }
}
function New-Events {
    $e = New-Expected
    return @(
        [pscustomobject]@{ event = 'dll_inspected'; sha256 = $e.ExpectedHookSha256; protocol_size = 480 },
        [pscustomobject]@{ event = 'binary'; dwmcore_sha256 = $e.DwmCoreSha256; allowlisted = $true },
        [pscustomobject]@{ event = 'preflight_complete'; eligible = $true; matching_dwm_processes = 1; target_writes = 0 },
        [pscustomobject]@{ event = 'target_identity_selected'; target_pid = $e.TargetProcessId; target_creation_filetime = $e.TargetCreationFileTime;
            target_exe = $e.TargetExe; explicitly_bound = $true; process_rights = 1052672 },
        [pscustomobject]@{ event = 'dwm_identity_selected'; dwm_pid = $e.DwmProcessId; dwm_creation_filetime = $e.DwmCreationFileTime; explicitly_bound = $true },
        [pscustomobject]@{ event = 'borrowed_identity_handles'; rights = 1052672; closed_after_start = $true; retained_for_incomplete_start = $false },
        [pscustomobject]@{ event = 'start'; completed = $true; status = 0; error = 0; dwm_pid = $e.DwmProcessId; target_pid = $e.TargetProcessId;
            qpc_frequency = 1000L; issued_qpc = 10000L; deadline_qpc = 15000L },
        (New-Telemetry 'observe' 10100 10095 10 0 2),
        (New-Telemetry 'observe' 10400 10395 20 0 2),
        (New-Telemetry 'observe' 10700 10695 30 0 2),
        (New-Telemetry 'expired' 15333 10695 40 2 4),
        (New-Telemetry 'recovery' 16350 10695 40 2 5),
        [pscustomobject]@{ event = 'complete'; started = $true; armed = $false; target_identity_retained = $true; dwm_identity_retained = $true;
            stop_completed = $true; stop_status = 0; vetoed_candidates = 0; read_faults = 0; active_callbacks = 0; no_post_expiry_veto = $true;
            managed = $false; continuous = $false; managed_error = 0; surface_rejected = $false; dll_loaded_retained = $true;
            hooks_available_for_session = $true; game_writes = 0; gamma_applied = $false; composition_confirmed = $false; vrr_confirmed = $false }
    )
}
function Get-Gate([object[]]$Events = (New-Events), [int]$ExitCode = 0, [bool]$Identity = $true,
    [bool]$Settings = $true, [bool]$CrashChecked = $true, [int]$Crashes = 0, [bool]$Timeout = $false) {
    return Test-9278ObserverEvidence $Events (New-Expected) $ExitCode $Identity $Settings $CrashChecked $Crashes $Timeout
}
function Reject-Mutation([string]$Event, [string]$Field, $Value) {
    $events = New-Events
    foreach ($item in @($events | Where-Object event -eq $Event)) { $item.$Field = $Value }
    $gate = Get-Gate $events
    if ($gate.Accepted) { throw "Unsafe evidence accepted: $Event/$Field" }
}

Check 'valid-complete-sdr-observer' {
    $r = Get-Gate
    if (-not $r.Accepted -or $r.Lifecycle -ne 'passed' -or $r.SelectedSdr -ne 'passed' -or $r.FreshCandidateSamples -ne 3 -or
        $r.GammaHdrVrrLatency -ne 'not tested by observer') { throw ($r | ConvertTo-Json -Depth 4) }
}
foreach ($case in @(
    @('hook-hash', 'dll_inspected', 'sha256', ('D' * 64)), @('wire-size', 'dll_inspected', 'protocol_size', 481),
    @('wrong-build', 'binary', 'dwmcore_sha256', ('D' * 64)), @('not-allowlisted', 'binary', 'allowlisted', $false),
    @('ambiguous-dwm', 'preflight_complete', 'matching_dwm_processes', 2), @('preflight-write', 'preflight_complete', 'target_writes', 1),
    @('target-pid', 'target_identity_selected', 'target_pid', 999), @('target-filetime', 'target_identity_selected', 'target_creation_filetime', 1L),
    @('target-exe', 'target_identity_selected', 'target_exe', 'F:\Other.exe'), @('target-unbound', 'target_identity_selected', 'explicitly_bound', $false),
    @('excessive-target-rights', 'target_identity_selected', 'process_rights', 2097151),
    @('dwm-generation', 'dwm_identity_selected', 'dwm_creation_filetime', 1L), @('dwm-unbound', 'dwm_identity_selected', 'explicitly_bound', $false),
    @('borrowed-handles-open', 'borrowed_identity_handles', 'closed_after_start', $false),
    @('incomplete-start', 'start', 'completed', $false), @('start-status', 'start', 'status', 5), @('start-error', 'start', 'error', 5),
    @('long-lease', 'start', 'deadline_qpc', 15001L), @('zero-frequency', 'start', 'qpc_frequency', 0L), @('reversed-lease', 'start', 'deadline_qpc', 9999L),
    @('veto-counter', 'telemetry', 'vetoed', 1), @('demote-counter', 'telemetry', 'demoted', 1), @('fault-counter', 'telemetry', 'read_faults', 1),
    @('win32-error', 'telemetry', 'last_win32_error', 5), @('demotion-capability', 'telemetry', 'capabilities', 15),
    @('incomplete-candidate', 'telemetry', 'sample_stage', 6), @('wrong-hook-point', 'telemetry', 'point', 2),
    @('wrong-window', 'telemetry', 'hwnd', 201L), @('wrong-owner', 'telemetry', 'owner_pid', 999),
    @('null-visual', 'telemetry', 'visual', 0L), @('null-surface', 'telemetry', 'surface', 0L), @('null-bind', 'telemetry', 'bind', 0L),
    @('pq-source', 'telemetry', 'colorspace', 12), @('scrgb-source', 'telemetry', 'format', 10),
    @('no-observation-proof', 'telemetry', 'flags', 28), @('no-ownership', 'telemetry', 'flags', 44),
    @('stale-samples', 'telemetry', 'sample_qpc', 9999L), @('future-samples', 'telemetry', 'sample_qpc', 20000L),
    @('zero-samples-exit-zero', 'telemetry', 'sample_valid', $false), @('no-expiry-callback', 'telemetry', 'expired_lease_calls', 0),
    @('unreturned-callback', 'telemetry', 'active_callbacks', 1), @('fake-boolean', 'telemetry', 'sample_valid', 'true'),
    @('null-counter', 'telemetry', 'read_faults', $null), @('string-counter', 'telemetry', 'read_faults', '0'),
    @('fractional-counter', 'telemetry', 'read_faults', 0.5), @('negative-counter', 'telemetry', 'candidate_calls', -1),
    @('no-stop', 'complete', 'stop_completed', $false), @('stop-status', 'complete', 'stop_status', 5),
    @('armed', 'complete', 'armed', $true), @('continuous', 'complete', 'continuous', $true), @('false-gamma-claim', 'complete', 'gamma_applied', $true),
    @('lost-target', 'complete', 'target_identity_retained', $false), @('lost-dwm', 'complete', 'dwm_identity_retained', $false)
)) {
    $captured = $case
    Check $case[0] { Reject-Mutation $captured[1] $captured[2] $captured[3] }
}
Check 'controller-exit' { if ((Get-Gate -ExitCode 20).Accepted) { throw 'Exit 20 accepted.' } }
Check 'controller-timeout' { if ((Get-Gate -Timeout $true).Accepted) { throw 'Timeout accepted.' } }
Check 'post-identity-loss' { if ((Get-Gate -Identity $false).Accepted) { throw 'Identity loss accepted.' } }
Check 'settings-mutated' { if ((Get-Gate -Settings $false).Accepted) { throw 'Settings change accepted.' } }
Check 'crash-query-failed' { if ((Get-Gate -CrashChecked $false).Accepted) { throw 'Missing event verification accepted.' } }
Check 'relevant-crash' { if ((Get-Gate -Crashes 1).Accepted) { throw 'Crash accepted.' } }
foreach ($name in @('start', 'complete', 'dll_inspected', 'borrowed_identity_handles', 'target_identity_selected', 'dwm_identity_selected')) {
    $eventName = $name
    Check "missing-$name" { if ((Get-Gate @(New-Events | Where-Object event -ne $eventName)).Accepted) { throw 'Missing event accepted.' } }
    Check "duplicate-$name" { $events = New-Events; $events += @($events | Where-Object event -eq $eventName)[0]; if ((Get-Gate $events).Accepted) { throw 'Duplicate event accepted.' } }
}
foreach ($name in @('veto_armed', 'lease_renewed', 'remote_call_incomplete', 'managed_go')) {
    $eventName = $name
    Check "forbidden-$name" { $events = New-Events; $events += [pscustomobject]@{ event = $eventName }; if ((Get-Gate $events).Accepted) { throw 'Forbidden event accepted.' } }
}
Check 'duplicate-not-fresh-samples' {
    $events = New-Events
    foreach ($t in @($events | Where-Object event -eq 'telemetry')) { $t.sample_qpc = 10095L }
    if ((Get-Gate $events).Accepted) { throw 'One repeated sample counted as three.' }
}
Check 'counter-regression' {
    $events = New-Events; $events[9].candidate_calls = 1
    if ((Get-Gate $events).Accepted) { throw 'Regressed counter accepted.' }
}
Check 'no-counter-progress' {
    $events = New-Events; foreach ($t in @($events | Where-Object event -eq 'telemetry')) { $t.candidate_calls = 10 }
    if ((Get-Gate $events).Accepted) { throw 'Non-progressing callbacks accepted.' }
}
Check 'recovery-not-stopped' { $events = New-Events; $events[11].lifecycle = 4; if ((Get-Gate $events).Accepted) { throw 'Not stopped.' } }
Check 'expiry-before-deadline' { $events = New-Events; $events[10].qpc = 14999L; if ((Get-Gate $events).Accepted) { throw 'Premature expiry accepted.' } }
Check 'expiry-counter-without-fresh-callback' {
    $events = New-Events; $events[10].last_callback_qpc = 14999L
    $r = Get-Gate $events
    if ($r.Accepted -or $r.Lifecycle -ne 'inconclusive') { throw 'Unexercised expiry accepted.' }
}
Check 'missing-stage-fails-closed' {
    $events = New-Events; $events[7].PSObject.Properties.Remove('sample_stage')
    if ((Get-Gate $events).Accepted) { throw 'Missing stage accepted.' }
}
Check 'completion-must-be-last' { $events = New-Events; $events = @($events[-1]) + $events[0..11]; if ((Get-Gate $events).Accepted) { throw 'Out-of-order completion accepted.' } }

Check 'argv-is-bounded-observer-only' {
    $argv = @(Get-ObserverArguments (New-Expected))
    if ($argv.Count -ne 17 -or $argv[0] -cne '--observe' -or $argv[-1] -cne '5000' -or
        @($argv | Where-Object { $_ -in @('--veto', '--continuous', '--owner-pid', '--pipe', '--validate-only') }).Count) { throw ($argv -join ' ') }
}
Check 'validation-argv-explicit' { $argv = @(Get-ObserverArguments (New-Expected) -Validation); if ($argv[-1] -cne '--validate-only') { throw 'Validation not explicit.' } }
Check 'argv-identity-rejection' { $e = New-Expected; $e.DwmProcessId = $e.TargetProcessId; MustThrow { Get-ObserverArguments $e } }
Check 'argv-native-quoting-spaces' {
    $quoted = ConvertTo-ObserverNativeArguments @('--dll', 'C:\Owned Candidate\HDRCalibrate.SelectiveDwmHook.dll')
    if ($quoted -cne '"--dll" "C:\Owned Candidate\HDRCalibrate.SelectiveDwmHook.dll"') { throw 'Incorrect quoting.' }
}
function New-ValidationEvent {
    $e = New-Expected
    return [pscustomobject]@{ event = 'request_validated'; target_pid = $e.TargetProcessId; target_hwnd = $e.TargetWindow;
        duration_ms = 5000; target_exe = $e.TargetExe; target_creation_filetime = $e.TargetCreationFileTime;
        target_identity_explicitly_bound = $true; owner_pid = 0; owner_creation_filetime = 0L; pipe = ''; continuous = $false;
        expected_dwm_pid = $e.DwmProcessId; expected_dwm_creation_filetime = $e.DwmCreationFileTime; process_access = $false; dll_loaded = $false }
}
Check 'validation-response-binds-all-fields' { Assert-ObserverValidation @(New-ValidationEvent) (New-Expected) 0 }
foreach ($pair in @(@('target_pid', 999), @('target_hwnd', 999L), @('target_creation_filetime', 1L),
    @('target_exe', 'F:\Other.exe'), @('expected_dwm_pid', 999), @('expected_dwm_creation_filetime', 1L),
    @('duration_ms', 10000), @('target_identity_explicitly_bound', $false), @('owner_pid', 1), @('owner_creation_filetime', 1L),
    @('pipe', 'unexpected'), @('continuous', $true), @('process_access', $true), @('dll_loaded', $true))) {
    $change = $pair
    Check ('validation-reject-' + $pair[0]) {
        $v = New-ValidationEvent; $v.($change[0]) = $change[1]
        MustThrow { Assert-ObserverValidation @($v) (New-Expected) 0 }
    }
}
Check 'validation-exit-failure' { MustThrow { Assert-ObserverValidation @(New-ValidationEvent) (New-Expected) 1 } }
Check 'validation-missing-evidence' { MustThrow { Assert-ObserverValidation @() (New-Expected) 0 } }
Check 'validation-duplicate-evidence' { MustThrow { Assert-ObserverValidation @((New-ValidationEvent), (New-ValidationEvent)) (New-Expected) 0 } }
foreach ($bad in @('', 'a"b', "a`nb", 'C:\trailing\')) {
    $badArgument = $bad; Check ('argv-reject-' + $results.Count) { MustThrow { ConvertTo-ObserverNativeArguments @($badArgument) } }
}
Check 'canonical-local-path' { if ((Get-ObserverCanonicalPath 'F:\Heroes\HeroesOfTheStorm_x64.exe' 'HeroesOfTheStorm_x64.exe') -cne 'F:\Heroes\HeroesOfTheStorm_x64.exe') { throw 'Changed canonical path.' } }
foreach ($bad in @('C:\', '.\relative.exe', 'C:relative.exe', '\\server\share\x.exe', '\\?\C:\x.exe', 'C:\x.exe:stream',
    'C:\dir\..\x.exe', 'C:\dir\.\x.exe', 'C:\dir\\x.exe', 'C:\dir.\x.exe', 'C:\dir \x.exe', 'C:\NUL\x.exe', 'C:\x*.exe', 'C:\"x.exe',
    'C:\dir/../x.exe', 'C:\x<.exe', 'C:\x|.exe')) {
    $badPath = $bad; Check ('path-reject-' + $results.Count) { MustThrow { Get-ObserverCanonicalPath $badPath } }
}
Check 'wrong-hook-basename' { MustThrow { Get-ObserverCanonicalPath 'C:\Candidate\renamed.dll' 'HDRCalibrate.SelectiveDwmHook.dll' } }
Check 'wrong-target-basename' { MustThrow { Get-ObserverCanonicalPath 'F:\Heroes\OtherGame.exe' 'HeroesOfTheStorm_x64.exe' } }

$localRoot = Join-Path ([IO.Path]::GetTempPath()) ('HDRCalibrate-ObserverOffline-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($localRoot)
$file = Join-Path $localRoot 'owned-fixture.bin'
try {
    [IO.File]::WriteAllBytes($file, [byte[]](1, 2, 3, 4))
    $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
    Check 'pinned-file-rejects-wrong-hash' { MustThrow { Open-ObserverPinnedFile $file ('0' * 64) } }
    Check 'pinned-file-denies-write-and-delete' {
        $pin = Open-ObserverPinnedFile $file $hash
        try {
            MustThrow { $writer = [IO.File]::Open($file, 'Open', 'Write', 'ReadWrite'); $writer.Dispose() }
            MustThrow { [IO.File]::Delete($file) }
            $reader = [IO.File]::Open($file, 'Open', 'Read', 'Read'); $reader.Dispose()
        } finally { $pin.Stream.Dispose() }
    }
    Check 'pin-released-after-validation' { $writer = [IO.File]::Open($file, 'Open', 'Write', 'Read'); $writer.Dispose() }
} finally {
    # Exact uniquely created test files only; no recursive deletion/globs.
    if (Test-Path -LiteralPath $file) { [IO.File]::Delete($file) }
    if (Test-Path -LiteralPath $localRoot) { [IO.Directory]::Delete($localRoot, $false) }
}

Check 'runner-default-is-validation' {
    $source = $ast.Extent.Text
    if ($source -notmatch "DefaultParameterSetName = 'Validate'" -or $source -notmatch 'if \(\$RunLive\)') { throw 'Live opt-in missing.' }
    $calls = @($ast.FindAll({ param($n) $n -is [Management.Automation.Language.CommandAst] -and $n.GetCommandName() -ceq 'Invoke-ObserverChild' }, $true))
    if ($calls.Count -ne 2) { throw 'Expected exactly validation plus one gated live invocation.' }
    $liveCall = @($calls | Where-Object { $_.Extent.Text -match '\$liveArguments' })
    if ($liveCall.Count -ne 1) { throw 'Ambiguous live invocation.' }
    $parent = $liveCall[0].Parent; $guarded = $false
    while ($parent) {
        if ($parent -is [Management.Automation.Language.IfStatementAst] -and $parent.Clauses[0].Item1.Extent.Text -ceq '$RunLive') { $guarded = $true }
        $parent = $parent.Parent
    }
    if (-not $guarded) { throw 'Live invocation is not guarded by explicit RunLive.' }
    if (@($ast.FindAll({ param($n) $n -is [Management.Automation.Language.CommandAst] -and $n.GetCommandName() -in @('Stop-Process', 'Restart-Computer') }, $true)).Count) { throw 'Forbidden broad process control in runner.' }
}
[ordered]@{ Passed = $results.Count; Cases = @($results.ToArray()); LiveActivation = $false; ProcessAccess = $false;
    NativeApisInitialized = $false; ControllerInvoked = $false } | ConvertTo-Json -Depth 5
