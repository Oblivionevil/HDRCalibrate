[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateRange(1, 2147483647)][int]$TargetProcessId,
    [Parameter(Mandatory = $true)][ValidateRange(1, [long]::MaxValue)][long]$TargetCreationFileTime,
    [Parameter(Mandatory = $true)][ValidateRange(1, [long]::MaxValue)][long]$TargetWindow,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [ValidateRange(100, 60000)][int]$DurationMs = 12000,
    [ValidateRange(20, 1000)][int]$IntervalMs = 50
)

# Read-only foreground evidence. No input, screenshots, activation, injection,
# process-memory access, registry changes or game/desktop control.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$tracePath = [IO.Path]::GetFullPath($OutputPath)
if (-not [IO.Path]::IsPathRooted($OutputPath) -or (Test-Path -LiteralPath $tracePath)) {
    throw 'A new absolute output file is required.'
}
$parent = [IO.Path]::GetDirectoryName($tracePath)
if (-not (Test-Path -LiteralPath $parent -PathType Container)) { throw 'Output directory must already exist.' }
$target = Get-Process -Id $TargetProcessId -ErrorAction Stop
try {
    if ($target.ProcessName -cne 'HeroesOfTheStorm_x64' -or
        $target.StartTime.ToUniversalTime().ToFileTimeUtc() -ne $TargetCreationFileTime -or
        $target.MainWindowHandle.ToInt64() -ne $TargetWindow) { throw 'HotS identity/window changed.' }
} finally { $target.Dispose() }

if (-not ('HdrCalibrateForegroundTrace' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class HdrCalibrateForegroundTrace {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
}
'@
}
$frequency = [Diagnostics.Stopwatch]::Frequency
$rows = [Collections.Generic.List[object]]::new()
$timer = [Diagnostics.Stopwatch]::StartNew()
do {
    $before = [Diagnostics.Stopwatch]::GetTimestamp()
    $window = [HdrCalibrateForegroundTrace]::GetForegroundWindow()
    $owner = [uint32]0
    $thread = [HdrCalibrateForegroundTrace]::GetWindowThreadProcessId($window, [ref]$owner)
    $after = [Diagnostics.Stopwatch]::GetTimestamp()
    $rows.Add([ordered]@{ QpcBefore = $before; QpcAfter = $after; Hwnd = $window.ToInt64();
        Pid = $owner; Valid = ($window -ne [IntPtr]::Zero -and $thread -ne 0);
        Selected = ($owner -eq $TargetProcessId -and $window.ToInt64() -eq $TargetWindow) })
    if ($timer.ElapsedMilliseconds -lt $DurationMs) { Start-Sleep -Milliseconds $IntervalMs }
} while ($timer.ElapsedMilliseconds -lt $DurationMs)
$timer.Stop()
$identityRetained = $false
$target = Get-Process -Id $TargetProcessId -ErrorAction SilentlyContinue
if ($target) {
    try { $identityRetained = $target.StartTime.ToUniversalTime().ToFileTimeUtc() -eq $TargetCreationFileTime }
    finally { $target.Dispose() }
}
$report = [ordered]@{ TargetProcessId = $TargetProcessId; TargetWindow = $TargetWindow;
    TargetCreationFileTime = $TargetCreationFileTime; QpcFrequency = $frequency;
    TargetIdentityRetained = $identityRetained; ReadOnly = $true; Samples = @($rows.ToArray()) }
$file = [IO.File]::Open($tracePath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
$writer = [IO.StreamWriter]::new($file, [Text.UTF8Encoding]::new($false))
try { $writer.Write(($report | ConvertTo-Json -Depth 5)) }
finally { $writer.Dispose() }
[pscustomobject]@{ OutputPath = $tracePath; Samples = $rows.Count;
    Selected = @($rows | Where-Object Selected).Count; TargetIdentityRetained = $identityRetained;
    ReadOnly = $true } | ConvertTo-Json -Compress
