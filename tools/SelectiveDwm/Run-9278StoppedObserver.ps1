[CmdletBinding(DefaultParameterSetName = 'Validate')]
param(
    [Parameter(Mandatory = $true)][string]$PreviousSummary,
    [string]$OutputRoot = ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\artifacts\observer-9278-stopped'))),
    [Parameter(ParameterSetName = 'Validate')][switch]$ValidateOnly,
    [Parameter(Mandatory = $true, ParameterSetName = 'Live')][switch]$RunLive
)

# Explicit follow-up to ONE completed first observer. No first-run fallback,
# replacement DLL, unload/restart, veto, continuous mode or automatic retry.
# Default validates prior evidence/local pinned binaries without process access.
# -RunLive still invokes native Start in DWM and needs explicit authorization.

function Get-StoppedObserverDefinitions([string]$Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') } finally { $sha.Dispose() }
        if ($hash -cne '0E8B63341F3DC57BBFDA1997F1AB2816C06A12CD6774448890A5E4E5EDCD6412') { throw 'First observer runner hash changed; no definitions imported.' }
        $stream.Position = 0
        $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8, $true, 4096, $true)
        try { $source = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $tokens = $null; $errors = $null
        $ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors)
        if ($errors.Count) { throw 'Pinned first observer source did not parse.' }
        $allowed = @('Get-ObserverCanonicalPath', 'Assert-ObserverNoReparse', 'Get-ObserverField', 'Get-ObserverNumber',
            'Get-ObserverBoolean', 'Assert-ObserverCondition', 'Get-ObserverOneEvent', 'Get-ObserverArguments',
            'ConvertTo-ObserverNativeArguments', 'Assert-ObserverValidation', 'Test-9278ObserverEvidence',
            'Open-ObserverPinnedFile', 'Read-ObserverEvents', 'Invoke-ObserverChild', 'Initialize-ObserverNative',
            'Assert-ObserverIdentity', 'Get-ObserverSettingsSnapshot', 'Get-ObserverCrashEvents')
        foreach ($name in $allowed) {
            $nodes = @($ast.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name }, $false))
            if ($nodes.Count -ne 1) { throw "Missing/ambiguous pinned helper: $name" }
            $nodes[0].Extent.Text
        }
    } finally { $stream.Dispose() }
}

function Get-StoppedObserverPrecursor($Summary, [object[]]$Events) {
    Assert-ObserverCondition ((Get-ObserverField $Summary 'Mode') -ceq 'RunLive' -and
        (Get-ObserverBoolean $Summary 'LiveStarted') -and (Get-ObserverBoolean $Summary 'PinnedHookMayRemain')) 'Precursor is not a completed first live observer.'
    Assert-ObserverCondition ($null -eq (Get-ObserverField $Summary 'Error')) 'Precursor has an error.'
    Assert-ObserverCondition (Get-ObserverBoolean $Summary 'PostIdentityRetained') 'Precursor lost process identities.'
    $controller = Get-ObserverField $Summary 'Controller'
    Assert-ObserverCondition ((Get-ObserverNumber $controller 'ExitCode') -eq 0 -and
        -not (Get-ObserverBoolean $controller 'TimedOut') -and -not (Get-ObserverBoolean $controller 'RemoteResourcesUnknown')) 'Precursor controller did not end cleanly.'
    Assert-ObserverCondition ((Get-ObserverNumber $controller 'ProcessId') -gt 0) 'Precursor controller identity is missing.'
    $before = Get-ObserverField $Summary 'SettingsBefore'; $after = Get-ObserverField $Summary 'SettingsAfter'
    Assert-ObserverCondition ($before -is [string] -and $after -is [string] -and $before.Length -gt 0 -and $before -ceq $after) 'Precursor settings/startup approval snapshot changed or is absent.'
    Assert-ObserverCondition (@(Get-ObserverField $Summary 'CrashEvents').Count -eq 0) 'Precursor contains a relevant crash event.'
    $identity = Get-ObserverField $Summary 'Identity'
    $expected = [ordered]@{}
    foreach ($name in @('ControllerPath', 'HookPath', 'TargetExe', 'ExpectedControllerSha256', 'ExpectedHookSha256', 'DwmCoreSha256', 'TargetExeSha256')) {
        $expected[$name] = Get-ObserverField $identity $name
    }
    foreach ($name in @('TargetProcessId', 'TargetWindow', 'TargetCreationFileTime', 'DwmProcessId', 'DwmCreationFileTime')) {
        $expected[$name] = Get-ObserverNumber $identity $name
        Assert-ObserverCondition ($expected[$name] -gt 0) "Missing precursor identity: $name"
    }
    Assert-ObserverCondition ($expected.TargetProcessId -le [int]::MaxValue -and $expected.DwmProcessId -le [int]::MaxValue) 'Precursor PID is out of range.'
    $expected.ControllerPath = Get-ObserverCanonicalPath $expected.ControllerPath 'HDRCalibrate.SelectiveController.exe'
    $expected.HookPath = Get-ObserverCanonicalPath $expected.HookPath 'HDRCalibrate.SelectiveDwmHook.dll'
    $expected.TargetExe = Get-ObserverCanonicalPath $expected.TargetExe 'HeroesOfTheStorm_x64.exe'
    Assert-ObserverCondition ($expected.ExpectedControllerSha256 -ieq '9E616F0949707DE0BF394FB1AE18575C4DC6BEFBF86A636C58A2D71DC8A10A3C' -and
        $expected.ExpectedHookSha256 -ieq '45F9C9B0F3776350C953C20587447708EFA03725EFB6BE9DC16EB78E8C824B63' -and
        $expected.DwmCoreSha256 -ieq '40579ECFA394395677FEC84014C32BA5495F334B6D96C5653FD5CAA129DC1222') 'Precursor does not use the frozen 9278 candidate bundle.'
    Assert-ObserverCondition ($expected.TargetExeSha256 -is [string] -and $expected.TargetExeSha256 -match '^[A-Fa-f0-9]{64}$') 'Precursor game EXE hash is missing.'
    [void](Get-ObserverArguments $expected -Validation)
    $savedEvents = @(Get-ObserverField $controller 'Events')
    $savedJson = ConvertTo-Json -InputObject $savedEvents -Depth 16 -Compress
    $actualJson = ConvertTo-Json -InputObject $Events -Depth 16 -Compress
    Assert-ObserverCondition ($savedJson -ceq $actualJson) 'Precursor summary events differ from its actual controller JSONL.'
    $gate = Test-9278ObserverEvidence $Events $expected 0 $true $true $true 0 $false
    $savedGate = Get-ObserverField $Summary 'Acceptance'
    Assert-ObserverCondition ($gate.Lifecycle -ceq 'passed' -and (Get-ObserverField $savedGate 'Lifecycle') -ceq 'passed') 'Precursor lifecycle/expiry/Stop proof failed.'
    Assert-ObserverCondition ($gate.SelectedSdr -ceq (Get-ObserverField $savedGate 'SelectedSdr') -and
        $gate.Accepted -eq (Get-ObserverBoolean $savedGate 'Accepted') -and $gate.Accepted -eq (Get-ObserverBoolean $Summary 'Accepted')) 'Precursor acceptance summary is inconsistent.'
    return [pscustomobject]@{ Expected = $expected; Gate = $gate; SettingsAfter = $after }
}

function Assert-StoppedObserverModules([string[]]$ModulePaths, [Collections.IDictionary]$Expected, [string]$DwmCorePath, [int]$ControllerCount) {
    Assert-ObserverCondition ($ControllerCount -eq 0) 'Another selective controller already exists.'
    $hooks = @($ModulePaths | Where-Object { [IO.Path]::GetFileName($_) -match '(?i)SelectiveDwmHook' })
    Assert-ObserverCondition ($hooks.Count -eq 1 -and $hooks[0] -ieq $Expected.HookPath) 'Exactly the already-loaded same-path hook is required; no new load or replacement is permitted.'
    Assert-ObserverCondition (@($ModulePaths | Where-Object { $_ -ieq $DwmCorePath }).Count -eq 1) 'Expected system dwmcore.dll is not uniquely loaded.'
}

function Assert-StoppedObserverExclusive([Collections.IDictionary]$Expected, [string]$DwmCorePath) {
    $controllers = @(Get-Process -Name 'HDRCalibrate.SelectiveController' -ErrorAction SilentlyContinue)
    try {
        $modules = [HdrObserver9278.Identity]::Modules($Expected.DwmProcessId)
        Assert-StoppedObserverModules $modules $Expected $DwmCorePath $controllers.Count
    } finally { foreach ($controller in $controllers) { $controller.Dispose() } }
}

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not [Environment]::Is64BitProcess) { throw 'Use 64-bit PowerShell.' }
foreach ($definition in @(Get-StoppedObserverDefinitions (Join-Path $PSScriptRoot 'Run-9278Observer.ps1'))) {
    # Definitions only, never the first runner's parameter/main execution body.
    . ([scriptblock]::Create($definition))
}
$PreviousSummary = Get-ObserverCanonicalPath $PreviousSummary 'summary.json'
$OutputRoot = Get-ObserverCanonicalPath $OutputRoot
$locks = [Collections.Generic.List[object]]::new(); $targetHandle = $null; $dwmHandle = $null
$mutex = $null; $mutexOwned = $false; $output = $null
$report = [ordered]@{ Mode = 'ValidateOnly'; Accepted = $false; LiveStarted = $false; PreviousSummary = $PreviousSummary;
    PreviousSummarySha256 = $null; PreviousControllerLogSha256 = $null; Precursor = $null; Identity = $null;
    OutputDirectory = $null; Validation = $null; Controller = $null; Acceptance = $null; Error = $null;
    SettingsBefore = $null; SettingsAfter = $null; CrashEvents = @(); PostIdentityRetained = $false;
    ExistingSamePathModuleRequired = $true; PinnedHookMayRemain = $true; GammaHdrVrrLatency = 'not tested by observer' }
try {
    $summaryPin = Open-ObserverPinnedFile $PreviousSummary ''
    $locks.Add($summaryPin); $report.PreviousSummarySha256 = $summaryPin.Sha256
    $previous = Get-Content -LiteralPath $PreviousSummary -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    $previousDirectory = [IO.Path]::GetDirectoryName($PreviousSummary)
    Assert-ObserverCondition ((Get-ObserverField $previous 'OutputDirectory') -ieq $previousDirectory) 'Precursor summary is not in its recorded evidence directory.'
    $logPath = Join-Path $previousDirectory 'controller.stdout.jsonl'
    $logPin = Open-ObserverPinnedFile $logPath ''
    $locks.Add($logPin); $report.PreviousControllerLogSha256 = $logPin.Sha256
    $precursor = Get-StoppedObserverPrecursor $previous @(Read-ObserverEvents $logPath)
    $expected = $precursor.Expected; $report.Precursor = $precursor.Gate; $report.Identity = $expected
    $locks.Add((Open-ObserverPinnedFile $expected.ControllerPath $expected.ExpectedControllerSha256))
    $locks.Add((Open-ObserverPinnedFile $expected.HookPath $expected.ExpectedHookSha256))
    Assert-ObserverNoReparse $OutputRoot
    if (-not (Test-Path -LiteralPath $OutputRoot)) { [void](New-Item -ItemType Directory -Path $OutputRoot -ErrorAction Stop) }
    $output = Join-Path $OutputRoot ('stopped-observer-{0}-{1}' -f [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'), [Guid]::NewGuid().ToString('N'))
    [void](New-Item -ItemType Directory -Path $output -ErrorAction Stop); $report.OutputDirectory = $output
    $validation = Invoke-ObserverChild $expected.ControllerPath @(Get-ObserverArguments $expected -Validation) $output 'validate'
    Assert-ObserverCondition (-not $validation.TimedOut) 'Local controller validation timed out.'
    Assert-ObserverValidation $validation.Events $expected $validation.ExitCode
    $report.Validation = $validation
    if ($RunLive) {
        $report.Mode = 'RunStoppedObserver'
        Initialize-ObserverNative
        $mutex = [Threading.Mutex]::new($false, 'Local\HDRCalibrate.First9278Observer')
        try { $mutexOwned = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $mutexOwned = $true; throw 'Previous observer owner ended abnormally; manual review required.' }
        Assert-ObserverCondition $mutexOwned 'Another observer runner is active.'
        $targetHandle = [HdrObserver9278.Identity]::new([uint32]$expected.TargetProcessId)
        $dwmHandle = [HdrObserver9278.Identity]::new([uint32]$expected.DwmProcessId)
        $dwmExe = Join-Path ([Environment]::SystemDirectory) 'dwm.exe'
        $dwmCore = Join-Path ([Environment]::SystemDirectory) 'dwmcore.dll'
        $locks.Add((Open-ObserverPinnedFile $dwmCore $expected.DwmCoreSha256))
        $locks.Add((Open-ObserverPinnedFile $expected.TargetExe $expected.TargetExeSha256))
        Assert-ObserverIdentity $targetHandle $dwmHandle $expected $dwmExe
        Assert-StoppedObserverExclusive $expected $dwmCore
        $report.SettingsBefore = Get-ObserverSettingsSnapshot
        Assert-ObserverCondition ($report.SettingsBefore -ceq $precursor.SettingsAfter) 'HDRCalibrate settings changed since the completed precursor.'
        $started = [DateTime]::UtcNow
        Assert-ObserverIdentity $targetHandle $dwmHandle $expected $dwmExe
        Assert-StoppedObserverExclusive $expected $dwmCore
        $report.LiveStarted = $true
        $report | ConvertTo-Json -Depth 16 | Out-File -LiteralPath (Join-Path $output 'preflight.json') -Encoding utf8
        # The frozen controller finds/reuses this existing module. It never
        # unloads it; Start itself refuses an already active session.
        $live = Invoke-ObserverChild $expected.ControllerPath @(Get-ObserverArguments $expected) $output 'controller'
        $report.Controller = $live
        try { Assert-ObserverIdentity $targetHandle $dwmHandle $expected $dwmExe; $report.PostIdentityRetained = $true } catch { $report.PostIdentityRetained = $false }
        $report.SettingsAfter = Get-ObserverSettingsSnapshot
        $crashChecked = $false
        try { $report.CrashEvents = @(Get-ObserverCrashEvents $started); $crashChecked = $true } catch { $report.Error = $_.Exception.Message }
        $report.Acceptance = Test-9278ObserverEvidence $live.Events $expected $live.ExitCode $report.PostIdentityRetained `
            ($report.SettingsBefore -ceq $report.SettingsAfter) $crashChecked $report.CrashEvents.Count $live.TimedOut
        $report.Accepted = $report.Acceptance.Accepted
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
