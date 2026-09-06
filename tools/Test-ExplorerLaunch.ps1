[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AppExe,
    [Parameter(Mandatory)][string]$ProbeExe,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$ExpectElevatedDesktop
)

$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Run with Windows PowerShell -STA.' }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try {
    if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this specific integration test elevated to verify the administrator-to-desktop handoff.'
    }
} finally { $identity.Dispose() }
$executable = (Resolve-Path -LiteralPath $AppExe).Path
$probe = (Resolve-Path -LiteralPath $ProbeExe).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
$artifactPrefix = [IO.Path]::GetFullPath((Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts')).TrimEnd('\') + '\'
if (-not $output.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Output must stay inside repository artifacts.' }
if ([IO.Path]::GetFileName($probe) -ne 'HDRCalibrate.LaunchProbe.exe') { throw 'Only the dedicated test probe is allowed.' }
[void][Reflection.Assembly]::LoadFrom((Join-Path (Split-Path $executable -Parent) 'HDRCalibrate.Dwm.dll'))
[void][Reflection.Assembly]::LoadFrom($executable)

# Only compatibility recognition is substituted for our dedicated test EXE.
# Actual privilege detection, process check, Explorer COM call and child token
# are real. Neither branch may ever launch a real browser in this test.
$inspectProbe = [Func[string,HDRCalibrate.Services.ProgramExecutableInfo]] {
    param($path)
    if ([IO.Path]::GetFileName($path) -ne 'HDRCalibrate.LaunchProbe.exe') { throw 'Unexpected launch target.' }
    $info = [HDRCalibrate.Services.ProgramExecutableInfo]::new()
    $info.IsSupported = $true
    return $info
}
$forbidDirect = [Action[Diagnostics.ProcessStartInfo]] { throw 'An elevated direct launch is forbidden.' }
$launcher = [HDRCalibrate.Services.ProgramProfileLauncher]::new($inspectProbe, $null, $forbidDirect, $null, $null)
foreach ($mode in @([HDRCalibrate.Services.ProgramColorMode]::Default, [HDRCalibrate.Services.ProgramColorMode]::Srgb, [HDRCalibrate.Services.ProgramColorMode]::ScrgbLinear)) {
    $fixture = Join-Path $output ('probe with spaces ' + $mode + '-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $fixture -Force | Out-Null
    $target = Join-Path $fixture 'HDRCalibrate.LaunchProbe.exe'
    Copy-Item -LiteralPath $probe -Destination $target
    Copy-Item -LiteralPath ($probe + '.config') -Destination ($target + '.config')
    $profile = [HDRCalibrate.Services.ProgramLaunchProfile]::new()
    $profile.ExecutablePath = $target
    $profile.ColorMode = $mode
    $launcher.Launch($profile)
    $resultFile = Join-Path $fixture 'launch-probe-result.xml'
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath $resultFile) -and $timer.Elapsed.TotalSeconds -lt 10) { Start-Sleep -Milliseconds 100 }
    if (-not (Test-Path -LiteralPath $resultFile)) { throw "No child result for $mode. Nothing is retried or force-terminated." }
    [xml]$result = Get-Content -LiteralPath $resultFile -Raw
    $expectedElevated = if ($ExpectElevatedDesktop) { 'true' } else { 'false' }
    $expectedIntegrity = if ($ExpectElevatedDesktop) { 0x3000 } else { 0x2000 }
    if ($result.LaunchProbe.Elevated -ne $expectedElevated -or [int]$result.LaunchProbe.IntegrityRid -ne $expectedIntegrity) { throw 'Child does not have the expected desktop privileges.' }
    if ([int]$result.LaunchProbe.SessionId -ne [Diagnostics.Process]::GetCurrentProcess().SessionId) { throw 'Wrong interactive session.' }
    if ($result.LaunchProbe.WorkingDirectory -ne $fixture) { throw 'Working directory changed.' }
    $expected = [HDRCalibrate.Services.ProgramProfileRules]::Arguments($mode)
    $arguments = @($result.LaunchProbe.Arguments.Argument | Where-Object { $null -ne $_ })
    if (($expected -eq '' -and $arguments.Count -ne 0) -or ($expected -ne '' -and ($arguments.Count -ne 1 -or $arguments[0] -ne $expected))) { throw 'Launch arguments were changed.' }
    $child = $null
    try { $child = [Diagnostics.Process]::GetProcessById([int]$result.LaunchProbe.ProcessId) }
    catch [ArgumentException] { }
    if ($null -ne $child) {
        try { if (-not $child.WaitForExit(5000)) { throw 'Test child did not exit; it will not be force-terminated.' } }
        finally { $child.Dispose() }
    }
    "PASS=$mode; PARENT_ELEVATED=True; CHILD_ELEVATED=$expectedElevated; INTEGRITY=$expectedIntegrity; REPORT=$resultFile"
}
'LIVE_BROWSER_STATE=untouched; no DWM changes'
