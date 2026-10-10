[CmdletBinding(DefaultParameterSetName = 'Validate')]
param(
    [Parameter(Mandatory = $true)][string]$ControllerPath,
    [Parameter(Mandatory = $true)][string]$HookPath,
    [Parameter(Mandatory = $true)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedControllerSha256,
    [Parameter(Mandatory = $true)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedHookSha256,
    [Parameter(Mandatory = $true)][ValidateRange(1, 2147483647)][int]$TargetProcessId,
    [Parameter(Mandatory = $true)][ValidateRange(1, [long]::MaxValue)][long]$TargetWindow,
    [Parameter(Mandatory = $true)][string]$TargetExe,
    [Parameter(Mandatory = $true)][ValidateRange(1, [long]::MaxValue)][long]$TargetCreationFileTime,
    [Parameter(Mandatory = $true)][ValidateRange(1, 2147483647)][int]$DwmProcessId,
    [Parameter(Mandatory = $true)][ValidateRange(1, [long]::MaxValue)][long]$DwmCreationFileTime,
    [string]$OutputRoot = ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\artifacts\observer-9278'))),
    [Parameter(ParameterSetName = 'Validate')][switch]$ValidateOnly,
    [Parameter(Mandatory = $true, ParameterSetName = 'Live')][switch]$RunLive
)

# First-observer acceptance only. Default mode does not open DWM or HotS.
# -RunLive requires separate authorization: it leaves a DLL/detours pinned in
# DWM after Stop. It NEVER applies gamma, vetoes, renews, restarts, unloads or
# launches/focuses/closes a game. Historical experiment runners remain unchanged.

function Get-ObserverCanonicalPath([string]$Path, [string]$RequiredName = '') {
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path -notmatch '^[A-Za-z]:\\' -or
        $Path -match '["\x00-\x1F*?<>|/]' -or $Path.Substring(2).Contains(':')) {
        throw 'An absolute local drive path without wildcards, ADS or control characters is required.'
    }
    $parts = $Path.Substring(3).Split('\')
    foreach ($part in $parts) {
        if (-not $part -or $part -in @('.', '..') -or $part -match '[. ]$' -or
            $part -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') {
            throw 'Empty, relative, device or ambiguously normalized path component.'
        }
    }
    $full = [IO.Path]::GetFullPath($Path)
    if ($full.Length -le 3) { throw 'A drive root is not an allowed target.' }
    if ($RequiredName -and [IO.Path]::GetFileName($full) -ine $RequiredName) {
        throw "Unexpected filename; required: $RequiredName."
    }
    return $full
}

function Assert-ObserverNoReparse([string]$Path) {
    $cursor = $Path
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse path refused: $cursor" }
        }
        $parent = [IO.Path]::GetDirectoryName($cursor)
        if ($parent -eq $cursor) { break }
        $cursor = $parent
    }
}

function Get-ObserverField($Object, [string]$Name) {
    if ($Object -is [Collections.IDictionary]) {
        if (-not $Object.Contains($Name)) { throw "Missing evidence field: $Name" }
        return $Object[$Name]
    }
    if ($null -eq $Object -or $null -eq $Object.PSObject.Properties[$Name]) { throw "Missing evidence field: $Name" }
    return $Object.PSObject.Properties[$Name].Value
}

function Get-ObserverNumber($Object, [string]$Name) {
    $value = Get-ObserverField $Object $Name
    if ($value -isnot [int] -and $value -isnot [long] -and $value -isnot [uint32] -and $value -isnot [uint64]) {
        throw "Non-integer evidence field: $Name"
    }
    if ($value -lt 0 -or [decimal]$value -gt [long]::MaxValue) { throw "Out-of-range evidence field: $Name" }
    return [long]$value
}

function Get-ObserverBoolean($Object, [string]$Name) {
    $value = Get-ObserverField $Object $Name
    if ($value -isnot [bool]) { throw "Non-boolean evidence field: $Name" }
    return $value
}

function Assert-ObserverCondition([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Get-ObserverOneEvent([object[]]$Events, [string]$Name) {
    $found = @($Events | Where-Object { (Get-ObserverField $_ 'event') -ceq $Name })
    if ($found.Count -ne 1) { throw "Expected exactly one $Name event, found $($found.Count)." }
    return $found[0]
}

function Get-ObserverArguments([Collections.IDictionary]$Expected, [switch]$Validation) {
    foreach ($name in @('TargetProcessId', 'TargetWindow', 'TargetCreationFileTime', 'DwmProcessId', 'DwmCreationFileTime')) {
        if ((Get-ObserverNumber $Expected $name) -le 0) { throw "Invalid bound identity: $name" }
    }
    if ($Expected.TargetProcessId -eq $Expected.DwmProcessId) { throw 'Target and DWM must be different processes.' }
    $dll = Get-ObserverCanonicalPath $Expected.HookPath 'HDRCalibrate.SelectiveDwmHook.dll'
    $exe = Get-ObserverCanonicalPath $Expected.TargetExe 'HeroesOfTheStorm_x64.exe'
    $argv = @('--observe', '--dll', $dll, '--target-pid', [string]$Expected.TargetProcessId,
        '--target-hwnd', [string]$Expected.TargetWindow, '--target-exe', $exe,
        '--target-creation-filetime', [string]$Expected.TargetCreationFileTime,
        '--expected-dwm-pid', [string]$Expected.DwmProcessId,
        '--expected-dwm-creation-filetime', [string]$Expected.DwmCreationFileTime, '--duration-ms', '5000')
    if ($Validation) { $argv += '--validate-only' }
    return $argv
}

function ConvertTo-ObserverNativeArguments([string[]]$Arguments) {
    return (@($Arguments | ForEach-Object {
        if ([string]::IsNullOrEmpty($_) -or $_ -match '["\x00-\x1F]' -or $_.EndsWith('\')) {
            throw 'Ambiguous native command-line argument refused.'
        }
        '"' + $_ + '"'
    }) -join ' ')
}

function Assert-ObserverValidation([object[]]$Events, [Collections.IDictionary]$Expected, [int]$ExitCode) {
    Assert-ObserverCondition ($ExitCode -eq 0 -and $Events.Count -eq 1) 'Controller argument validation failed.'
    $v = Get-ObserverOneEvent $Events 'request_validated'
    foreach ($pair in @(@('target_pid', 'TargetProcessId'), @('target_hwnd', 'TargetWindow'),
        @('target_creation_filetime', 'TargetCreationFileTime'), @('expected_dwm_pid', 'DwmProcessId'),
        @('expected_dwm_creation_filetime', 'DwmCreationFileTime'))) {
        Assert-ObserverCondition ((Get-ObserverNumber $v $pair[0]) -eq $Expected[$pair[1]]) "Validation identity mismatch: $($pair[0])."
    }
    Assert-ObserverCondition ((Get-ObserverField $v 'target_exe') -ieq $Expected.TargetExe) 'Validation EXE mismatch.'
    Assert-ObserverCondition ((Get-ObserverNumber $v 'duration_ms') -eq 5000) 'Validation lease mismatch.'
    Assert-ObserverCondition (Get-ObserverBoolean $v 'target_identity_explicitly_bound') 'Target identity was not bound.'
    foreach ($name in @('continuous', 'process_access', 'dll_loaded')) {
        Assert-ObserverCondition (-not (Get-ObserverBoolean $v $name)) "Unexpected validation action: $name."
    }
    Assert-ObserverCondition ((Get-ObserverNumber $v 'owner_pid') -eq 0 -and (Get-ObserverNumber $v 'owner_creation_filetime') -eq 0 -and
        (Get-ObserverField $v 'pipe') -ceq '') 'Unexpected managed/continuous owner.'
}

function Test-9278ObserverEvidence([object[]]$Events, [Collections.IDictionary]$Expected, [int]$ExitCode,
    [bool]$PostIdentityRetained, [bool]$SettingsUnchanged, [bool]$CrashCheckCompleted, [int]$RelevantCrashCount,
    [bool]$TimedOut = $false) {
    $result = [ordered]@{ Accepted = $false; Lifecycle = 'inconclusive'; SelectedSdr = 'not observed';
        FreshCandidateSamples = 0; ObservedBeforeArm = $false; ExpiryExercised = $false;
        GammaHdrVrrLatency = 'not tested by observer'; Reasons = @() }
    try {
        Assert-ObserverCondition (-not $TimedOut -and $ExitCode -eq 0) 'Controller failed or timed out; retained remote resources may be unknown.'
        Assert-ObserverCondition ($PostIdentityRetained) 'Post-run process/window identities or responsiveness were not retained.'
        Assert-ObserverCondition ($SettingsUnchanged) 'HDRCalibrate state/startup approvals changed during observation.'
        Assert-ObserverCondition ($CrashCheckCompleted -and $RelevantCrashCount -eq 0) 'Crash/event verification failed or found a relevant event.'
        foreach ($event in $Events) {
            $name = Get-ObserverField $event 'event'
            Assert-ObserverCondition ($name -notin @('veto_armed', 'lease_renewed', 'remote_call_incomplete', 'managed_go', 'managed_connected')) "Forbidden observer event: $name."
        }
        $dll = Get-ObserverOneEvent $Events 'dll_inspected'
        Assert-ObserverCondition ((Get-ObserverField $dll 'sha256') -ieq $Expected.ExpectedHookSha256 -and
            (Get-ObserverNumber $dll 'protocol_size') -eq 480) 'Hook hash/protocol identity mismatch.'
        $binary = Get-ObserverOneEvent $Events 'binary'
        Assert-ObserverCondition ((Get-ObserverField $binary 'dwmcore_sha256') -ieq $Expected.DwmCoreSha256 -and
            (Get-ObserverBoolean $binary 'allowlisted')) 'DWM core binary preflight mismatch.'
        $preflight = Get-ObserverOneEvent $Events 'preflight_complete'
        Assert-ObserverCondition ((Get-ObserverBoolean $preflight 'eligible') -and
            (Get-ObserverNumber $preflight 'matching_dwm_processes') -eq 1 -and (Get-ObserverNumber $preflight 'target_writes') -eq 0) 'DWM preflight not uniquely eligible.'
        $target = Get-ObserverOneEvent $Events 'target_identity_selected'
        Assert-ObserverCondition ((Get-ObserverNumber $target 'target_pid') -eq $Expected.TargetProcessId -and
            (Get-ObserverNumber $target 'target_creation_filetime') -eq $Expected.TargetCreationFileTime -and
            (Get-ObserverField $target 'target_exe') -ieq $Expected.TargetExe -and
            (Get-ObserverBoolean $target 'explicitly_bound') -and (Get-ObserverNumber $target 'process_rights') -eq 1052672) 'Target binding/rights mismatch.'
        $dwm = Get-ObserverOneEvent $Events 'dwm_identity_selected'
        Assert-ObserverCondition ((Get-ObserverNumber $dwm 'dwm_pid') -eq $Expected.DwmProcessId -and
            (Get-ObserverNumber $dwm 'dwm_creation_filetime') -eq $Expected.DwmCreationFileTime -and
            (Get-ObserverBoolean $dwm 'explicitly_bound')) 'DWM generation binding mismatch.'
        $handles = Get-ObserverOneEvent $Events 'borrowed_identity_handles'
        Assert-ObserverCondition ((Get-ObserverBoolean $handles 'closed_after_start') -and
            -not (Get-ObserverBoolean $handles 'retained_for_incomplete_start') -and
            (Get-ObserverNumber $handles 'rights') -eq 1052672) 'Borrowed metadata handles not closed safely.'
        $start = Get-ObserverOneEvent $Events 'start'
        Assert-ObserverCondition ((Get-ObserverBoolean $start 'completed') -and (Get-ObserverNumber $start 'status') -eq 0 -and
            (Get-ObserverNumber $start 'error') -eq 0 -and (Get-ObserverNumber $start 'dwm_pid') -eq $Expected.DwmProcessId -and
            (Get-ObserverNumber $start 'target_pid') -eq $Expected.TargetProcessId) 'Start failed or started for different identities.'
        $issued = Get-ObserverNumber $start 'issued_qpc'; $deadline = Get-ObserverNumber $start 'deadline_qpc'
        $frequency = Get-ObserverNumber $start 'qpc_frequency'
        Assert-ObserverCondition ($frequency -gt 0 -and $issued -gt 0 -and $deadline -gt $issued -and
            [decimal]$deadline - [decimal]$issued -le [decimal]$frequency * 5) 'Lease is absent, reversed or exceeds five seconds.'
        $telemetry = @($Events | Where-Object { (Get-ObserverField $_ 'event') -ceq 'telemetry' })
        Assert-ObserverCondition ($telemetry.Count -ge 3) 'Insufficient telemetry.'
        $seen = [Collections.Generic.HashSet[long]]::new(); $previous = @{}; $previousQpc = 0L
        $firstCandidateCalls = Get-ObserverNumber $telemetry[0] 'candidate_calls'
        $expiryCount = 0L; $expiredRows = 0; $recoveries = @()
        foreach ($t in $telemetry) {
            $qpc = Get-ObserverNumber $t 'qpc'; $phase = Get-ObserverField $t 'phase'
            Assert-ObserverCondition ($qpc -ge $issued -and $qpc -gt $previousQpc) 'Non-monotonic telemetry timestamps.'
            $previousQpc = $qpc
            Assert-ObserverCondition ($phase -in @('observe', 'expired', 'recovery')) 'Unexpected telemetry phase.'
            foreach ($counter in @('vetoed', 'demoted', 'read_faults', 'last_win32_error')) {
                Assert-ObserverCondition ((Get-ObserverNumber $t $counter) -eq 0) "Unsafe observer telemetry: $counter."
            }
            $caps = Get-ObserverNumber $t 'capabilities'
            Assert-ObserverCondition (($caps -band 3) -eq 3 -and ($caps -band 8) -eq 0) 'Missing observer or unsafe demotion capability.'
            foreach ($counter in @('candidate_calls', 'iflip_calls', 'target_matches', 'sdr_matches', 'expired_lease_calls')) {
                $value = Get-ObserverNumber $t $counter
                if ($previous.ContainsKey($counter)) { Assert-ObserverCondition ($value -ge $previous[$counter]) "Counter regressed: $counter." }
                $previous[$counter] = $value
            }
            $active = Get-ObserverNumber $t 'active_callbacks'
            $lifecycle = Get-ObserverNumber $t 'lifecycle'
            Assert-ObserverCondition ($lifecycle -in @(1, 2, 4, 5)) 'Unexpected observer lifecycle.'
            $callback = Get-ObserverNumber $t 'last_callback_qpc'
            Assert-ObserverCondition ($callback -le $qpc) 'Callback timestamp is in the future.'
            if ($phase -eq 'observe') {
                Assert-ObserverCondition ($qpc -lt $deadline -and $lifecycle -in @(1, 2, 4)) 'Observe phase outside its lease.'
                Assert-ObserverCondition ($previous.expired_lease_calls -eq 0) 'Expiry counter incremented before deadline.'
                $valid = Get-ObserverBoolean $t 'sample_valid'
                if ($valid) {
                    $sampleQpc = Get-ObserverNumber $t 'sample_qpc'
                    $flags = Get-ObserverNumber $t 'flags'
                    if ((Get-ObserverNumber $t 'decision') -eq 13 -and (Get-ObserverNumber $t 'owner_pid') -eq $Expected.TargetProcessId) {
                        $result.SelectedSdr = 'unsupported'
                    }
                    $fresh = $sampleQpc -ge $issued -and $sampleQpc -lt $deadline -and $sampleQpc -le $qpc -and $qpc - $sampleQpc -lt $frequency
                    $selected = (Get-ObserverNumber $t 'point') -eq 1 -and (Get-ObserverNumber $t 'sample_stage') -eq 7 -and
                        (Get-ObserverNumber $t 'decision') -eq 0 -and (Get-ObserverNumber $t 'owner_pid') -eq $Expected.TargetProcessId -and
                        (Get-ObserverNumber $t 'hwnd') -eq $Expected.TargetWindow -and (Get-ObserverNumber $t 'visual') -gt 0 -and
                        (Get-ObserverNumber $t 'surface') -gt 0 -and (Get-ObserverNumber $t 'bind') -gt 0 -and
                        (Get-ObserverNumber $t 'colorspace') -eq 0 -and (Get-ObserverNumber $t 'format') -in @(28, 87, 88) -and ($flags -band 28) -eq 28
                    if ($fresh -and $selected -and $seen.Add($sampleQpc)) {
                        $result.FreshCandidateSamples++
                        if (($flags -band 32) -ne 0) { $result.ObservedBeforeArm = $true }
                    }
                }
            } elseif ($phase -eq 'expired') {
                $expiredRows++
                Assert-ObserverCondition ($qpc -ge $deadline) 'Expiry reported before deadline.'
                $expiryCount = $previous.expired_lease_calls
                $result.ExpiryExercised = $expiryCount -gt 0 -and $callback -ge $deadline
            } else {
                $recoveries += $t
                Assert-ObserverCondition ($qpc -ge $deadline -and $lifecycle -eq 5 -and $active -eq 0) 'Stop recovery did not drain to StoppedPinned.'
            }
        }
        Assert-ObserverCondition ($expiredRows -eq 1 -and $recoveries.Count -eq 1 -and
            (Get-ObserverField $telemetry[-1] 'phase') -ceq 'recovery') 'Missing/duplicate expiry or final recovery.'
        $complete = Get-ObserverOneEvent $Events 'complete'
        Assert-ObserverCondition ((Get-ObserverField $Events[-1] 'event') -ceq 'complete' -and
            [array]::IndexOf($Events, $start) -lt [array]::IndexOf($Events, $telemetry[0])) 'Completion/start event order is invalid.'
        foreach ($name in @('started', 'target_identity_retained', 'dwm_identity_retained', 'stop_completed', 'no_post_expiry_veto', 'dll_loaded_retained', 'hooks_available_for_session')) {
            Assert-ObserverCondition (Get-ObserverBoolean $complete $name) "Incomplete observer safety result: $name."
        }
        foreach ($name in @('armed', 'managed', 'continuous', 'surface_rejected', 'gamma_applied', 'composition_confirmed', 'vrr_confirmed')) {
            Assert-ObserverCondition (-not (Get-ObserverBoolean $complete $name)) "Unexpected observer claim/action: $name."
        }
        foreach ($name in @('stop_status', 'vetoed_candidates', 'read_faults', 'active_callbacks', 'managed_error', 'game_writes')) {
            Assert-ObserverCondition ((Get-ObserverNumber $complete $name) -eq 0) "Failed completion field: $name."
        }
        if ($result.FreshCandidateSamples -ge 3 -and $result.ObservedBeforeArm -and
            $previous.candidate_calls -ge 3 -and $previous.candidate_calls -gt $firstCandidateCalls -and
            $previous.target_matches -ge 3 -and $previous.sdr_matches -ge 3) { $result.SelectedSdr = 'passed' }
        if ($result.ExpiryExercised) { $result.Lifecycle = 'passed' }
        else { $result.Reasons += 'No completed callback exercised natural lease expiry.' }
        if ($result.SelectedSdr -ne 'passed') { $result.Reasons += 'Three fresh completed selected SDR candidate samples plus native observation proof were not obtained.' }
        $result.Accepted = $result.Lifecycle -eq 'passed' -and $result.SelectedSdr -eq 'passed'
    } catch {
        $result.Lifecycle = 'failed'; $result.Reasons += $_.Exception.Message
    }
    return [pscustomobject]$result
}

function Open-ObserverPinnedFile([string]$Path, [string]$ExpectedHash) {
    Assert-ObserverNoReparse $Path
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') } finally { $sha.Dispose() }
        if ($ExpectedHash -and $hash -ine $ExpectedHash) { throw "Pinned SHA256 mismatch: $Path" }
        $stream.Position = 0
        return [pscustomobject]@{ Path = $Path; Sha256 = $hash; Stream = $stream }
    } catch { $stream.Dispose(); throw }
}

function Read-ObserverEvents([string]$Path) {
    $events = @()
    foreach ($line in Get-Content -LiteralPath $Path -ErrorAction Stop) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $event = $line | ConvertFrom-Json -ErrorAction Stop
        [void](Get-ObserverField $event 'event')
        $events += $event
    }
    return $events
}

function Invoke-ObserverChild([string]$Path, [string[]]$Arguments, [string]$Directory, [string]$Label) {
    $child = Start-Process -FilePath $Path -ArgumentList (ConvertTo-ObserverNativeArguments $Arguments) -WindowStyle Hidden -PassThru `
        -WorkingDirectory ([IO.Path]::GetDirectoryName($Path)) -RedirectStandardOutput (Join-Path $Directory "$Label.stdout.jsonl") `
        -RedirectStandardError (Join-Path $Directory "$Label.stderr.log")
    try {
        # Retain this exact process object/handle; never rediscover a helper by PID.
        [void]$child.Handle
        $timedOut = -not $child.WaitForExit(30000)
        if ($timedOut) {
            $child.Refresh()
            if (-not $child.HasExited -and -not $child.WaitForExit(1000)) { $child.Kill(); [void]$child.WaitForExit(3000) }
        }
        $child.Refresh()
        if (-not $child.HasExited) { throw "Owned controller $($child.Id) is still live after timeout; inspect its exact handle/logs. No DWM/game cleanup was attempted." }
        $child.WaitForExit()
        return [pscustomobject]@{ ProcessId = $child.Id; ExitCode = $child.ExitCode; TimedOut = $timedOut;
            RemoteResourcesUnknown = $timedOut; Events = @(Read-ObserverEvents (Join-Path $Directory "$Label.stdout.jsonl")) }
    } finally { $child.Dispose() }
}

function Initialize-ObserverNative {
    if ('HdrObserver9278.Identity' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;
namespace HdrObserver9278 {
 public sealed class Identity : IDisposable {
  private IntPtr handle;
  public readonly uint Pid;
  public Identity(uint pid) { Pid=pid; handle=OpenProcess(0x101000, false, pid); if(handle==IntPtr.Zero) throw new Win32Exception(); }
  public ulong Creation() { long c,e,k,u; if(!GetProcessTimes(handle,out c,out e,out k,out u)) throw new Win32Exception(); return unchecked((ulong)c); }
  public string Path() { var b=new StringBuilder(32768); int n=b.Capacity; if(!QueryFullProcessImageName(handle,0,b,ref n)) throw new Win32Exception(); return b.ToString(); }
  public uint Session() { uint s; if(!ProcessIdToSessionId(Pid,out s)) throw new Win32Exception(); return s; }
  public bool Alive() { return handle!=IntPtr.Zero && WaitForSingleObject(handle,0)==258 && GetProcessId(handle)==Pid; }
  public void Dispose() { if(handle!=IntPtr.Zero) { CloseHandle(handle); handle=IntPtr.Zero; } }
  public static bool Window(uint pid,long hwnd) { uint owner; return hwnd!=0 && GetWindowThreadProcessId(new IntPtr(hwnd),out owner)!=0 && owner==pid && !IsHungAppWindow(new IntPtr(hwnd)); }
  public static string[] Modules(uint pid) {
   IntPtr snapshot=CreateToolhelp32Snapshot(0x18,pid); if(snapshot==new IntPtr(-1)) throw new Win32Exception();
   try { var result=new List<string>(); var m=new ModuleEntry(); m.Size=(uint)Marshal.SizeOf(typeof(ModuleEntry));
    if(!Module32First(snapshot,ref m)) throw new Win32Exception();
    do { result.Add(m.Path); m.Size=(uint)Marshal.SizeOf(typeof(ModuleEntry)); } while(Module32Next(snapshot,ref m));
    if(Marshal.GetLastWin32Error()!=18) throw new Win32Exception(); return result.ToArray();
   } finally { CloseHandle(snapshot); }
  }
  [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct ModuleEntry { public uint Size,ModuleId,ProcessId,GlobalUsage,ProcessUsage; public IntPtr Base; public uint BaseSize; public IntPtr Module; [MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)] public string Name; [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)] public string Path; }
  [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenProcess(uint access,bool inherit,uint pid);
  [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetProcessTimes(IntPtr p,out long c,out long e,out long k,out long u);
  [DllImport("kernel32.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr p,uint flags,StringBuilder path,ref int size);
  [DllImport("kernel32.dll",SetLastError=true)] static extern bool ProcessIdToSessionId(uint pid,out uint session);
  [DllImport("kernel32.dll",SetLastError=true)] static extern uint WaitForSingleObject(IntPtr p,uint timeout);
  [DllImport("kernel32.dll",SetLastError=true)] static extern uint GetProcessId(IntPtr p);
  [DllImport("kernel32.dll",SetLastError=true)] static extern bool CloseHandle(IntPtr p);
  [DllImport("user32.dll",SetLastError=true)] static extern uint GetWindowThreadProcessId(IntPtr w,out uint pid);
  [DllImport("user32.dll")] static extern bool IsHungAppWindow(IntPtr w);
  [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags,uint pid);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool Module32First(IntPtr snapshot,ref ModuleEntry entry);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool Module32Next(IntPtr snapshot,ref ModuleEntry entry);
 }
}
'@
}

function Assert-ObserverIdentity($Target, $Dwm, [Collections.IDictionary]$Expected, [string]$DwmExe) {
    $session = [Diagnostics.Process]::GetCurrentProcess().SessionId
    Assert-ObserverCondition ($Target.Alive() -and $Dwm.Alive() -and $Target.Creation() -eq $Expected.TargetCreationFileTime -and
        $Dwm.Creation() -eq $Expected.DwmCreationFileTime -and $Target.Path() -ieq $Expected.TargetExe -and $Dwm.Path() -ieq $DwmExe -and
        $Target.Session() -eq $session -and $Dwm.Session() -eq $session -and
        [HdrObserver9278.Identity]::Window($Expected.TargetProcessId, $Expected.TargetWindow)) 'Bound target/DWM identity, session or window changed.'
    $process = Get-Process -Id $Expected.DwmProcessId -ErrorAction Stop
    try { Assert-ObserverCondition ($process.Responding) 'DWM is not responding.' } finally { $process.Dispose() }
}

function Assert-ObserverExclusive([Collections.IDictionary]$Expected, [string]$DwmCorePath) {
    $controllers = @(Get-Process -Name 'HDRCalibrate.SelectiveController' -ErrorAction SilentlyContinue)
    try { Assert-ObserverCondition ($controllers.Count -eq 0) 'Another selective controller already exists.' }
    finally { foreach ($controller in $controllers) { $controller.Dispose() } }
    $modules = [HdrObserver9278.Identity]::Modules($Expected.DwmProcessId)
    Assert-ObserverCondition (@($modules | Where-Object { [IO.Path]::GetFileName($_) -match '(?i)SelectiveDwmHook' }).Count -eq 0) 'A selective hook is already loaded; first-observer reuse/unload is forbidden.'
    Assert-ObserverCondition (@($modules | Where-Object { $_ -ieq $DwmCorePath }).Count -eq 1) 'Expected system dwmcore.dll is not uniquely loaded.'
}

function Get-ObserverSettingsSnapshot {
    $root = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'HDRCalibrate'
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { return '[]' }
    Assert-ObserverNoReparse $root
    $records = @(Get-ChildItem -LiteralPath $root -File -Force | Sort-Object Name | ForEach-Object {
        Assert-ObserverNoReparse $_.FullName
        [ordered]@{ Name = $_.Name; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256 -ErrorAction Stop).Hash }
    })
    return (ConvertTo-Json -InputObject $records -Compress -Depth 3)
}

function Get-ObserverCrashEvents([DateTime]$Since) {
    $found = @()
    foreach ($log in @('Application', 'System')) {
        try { $rows = @(Get-WinEvent -FilterHashtable @{ LogName = $log; StartTime = $Since; Level = @(1, 2, 3) } -ErrorAction Stop) }
        catch { if ($_.FullyQualifiedErrorId -like 'NoMatchingEventsFound*') { $rows = @() } else { throw } }
        foreach ($row in $rows) {
            if (($log -eq 'Application' -and $row.ProviderName -in @('Application Error', 'Application Hang', '.NET Runtime', 'Windows Error Reporting') -and
                $row.Message -match '(?i)HeroesOfTheStorm|dwm\.exe|dwmcore\.dll|HDRCalibrate') -or
                ($log -eq 'System' -and $row.ProviderName -in @('Display', 'nvlddmkm'))) {
                $found += [ordered]@{ Log = $log; TimeCreated = $row.TimeCreated.ToUniversalTime().ToString('o'); Provider = $row.ProviderName; Id = $row.Id; RecordId = $row.RecordId }
            }
        }
    }
    return $found
}

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not [Environment]::Is64BitProcess) { throw 'Use 64-bit PowerShell.' }
$ControllerPath = Get-ObserverCanonicalPath $ControllerPath 'HDRCalibrate.SelectiveController.exe'
$HookPath = Get-ObserverCanonicalPath $HookPath 'HDRCalibrate.SelectiveDwmHook.dll'
$TargetExe = Get-ObserverCanonicalPath $TargetExe 'HeroesOfTheStorm_x64.exe'
$OutputRoot = Get-ObserverCanonicalPath $OutputRoot
$expected = [ordered]@{ ControllerPath = $ControllerPath; HookPath = $HookPath; ExpectedHookSha256 = $ExpectedHookSha256.ToUpperInvariant();
    ExpectedControllerSha256 = $ExpectedControllerSha256.ToUpperInvariant(); TargetProcessId = $TargetProcessId; TargetWindow = $TargetWindow;
    TargetExe = $TargetExe; TargetCreationFileTime = $TargetCreationFileTime; DwmProcessId = $DwmProcessId; DwmCreationFileTime = $DwmCreationFileTime;
    DwmCoreSha256 = '40579ECFA394395677FEC84014C32BA5495F334B6D96C5653FD5CAA129DC1222' }
$validationArguments = @(Get-ObserverArguments $expected -Validation)
$liveArguments = @(Get-ObserverArguments $expected)
$locks = [Collections.Generic.List[object]]::new(); $targetHandle = $null; $dwmHandle = $null; $mutex = $null; $mutexOwned = $false
$output = $null; $report = [ordered]@{ Mode = 'ValidateOnly'; Accepted = $false; LiveStarted = $false; Identity = $expected;
    OutputDirectory = $null; Validation = $null; Controller = $null; Acceptance = $null; Error = $null;
    SettingsBefore = $null; SettingsAfter = $null; CrashEvents = @(); PostIdentityRetained = $false;
    GammaHdrVrrLatency = 'not tested by observer'; PinnedHookMayRemain = $false }
try {
    $locks.Add((Open-ObserverPinnedFile $ControllerPath $ExpectedControllerSha256))
    $locks.Add((Open-ObserverPinnedFile $HookPath $ExpectedHookSha256))
    Assert-ObserverNoReparse $OutputRoot
    if (-not (Test-Path -LiteralPath $OutputRoot)) { [void](New-Item -ItemType Directory -Path $OutputRoot -ErrorAction Stop) }
    $output = Join-Path $OutputRoot ('observer-{0}-{1}' -f [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'), [Guid]::NewGuid().ToString('N'))
    [void](New-Item -ItemType Directory -Path $output -ErrorAction Stop)
    $report.OutputDirectory = $output
    $validation = Invoke-ObserverChild $ControllerPath $validationArguments $output 'validate'
    Assert-ObserverCondition (-not $validation.TimedOut) 'Local controller validation timed out.'
    Assert-ObserverValidation $validation.Events $expected $validation.ExitCode
    $report.Validation = $validation
    if ($RunLive) {
        $report.Mode = 'RunLive'
        Initialize-ObserverNative
        $mutex = [Threading.Mutex]::new($false, 'Local\HDRCalibrate.First9278Observer')
        try { $mutexOwned = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $mutexOwned = $true; throw 'Previous observer owner ended abnormally; manual review required.' }
        Assert-ObserverCondition $mutexOwned 'Another first-observer runner is active.'
        $targetHandle = [HdrObserver9278.Identity]::new([uint32]$TargetProcessId)
        $dwmHandle = [HdrObserver9278.Identity]::new([uint32]$DwmProcessId)
        $dwmExe = Join-Path ([Environment]::SystemDirectory) 'dwm.exe'
        $dwmCore = Join-Path ([Environment]::SystemDirectory) 'dwmcore.dll'
        $locks.Add((Open-ObserverPinnedFile $dwmCore $expected.DwmCoreSha256))
        $targetPin = Open-ObserverPinnedFile $TargetExe ''
        $locks.Add($targetPin); $report.Identity.TargetExeSha256 = $targetPin.Sha256
        $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($dwmCore)
        Assert-ObserverCondition ($version.FileMajorPart -eq 10 -and $version.FileMinorPart -eq 0 -and
            $version.FileBuildPart -eq 26100 -and $version.FilePrivatePart -eq 9278) 'This runner only accepts dwmcore 10.0.26100.9278.'
        Assert-ObserverIdentity $targetHandle $dwmHandle $expected $dwmExe
        Assert-ObserverExclusive $expected $dwmCore
        $report.SettingsBefore = Get-ObserverSettingsSnapshot
        $started = [DateTime]::UtcNow
        # Repeat directly before the one and only live invocation. No retry.
        Assert-ObserverIdentity $targetHandle $dwmHandle $expected $dwmExe
        Assert-ObserverExclusive $expected $dwmCore
        $report.LiveStarted = $true; $report.PinnedHookMayRemain = $true
        $report | ConvertTo-Json -Depth 12 | Out-File -LiteralPath (Join-Path $output 'preflight.json') -Encoding utf8
        $live = Invoke-ObserverChild $ControllerPath $liveArguments $output 'controller'
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
    if ($output) { $report | ConvertTo-Json -Depth 16 | Out-File -LiteralPath (Join-Path $output 'summary.json') -Encoding utf8 }
}
$report | ConvertTo-Json -Depth 16
if ($report.Error -or ($RunLive -and -not $report.Accepted)) { exit 1 }
