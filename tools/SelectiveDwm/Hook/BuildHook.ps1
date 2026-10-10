[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..\..')).Path,
    [string]$OutputDirectory = '',
    [string]$MinHookDirectory = '',
    [string]$MinHookLibrary = '',
    [switch]$RunTests,
    [switch]$HostTestsOnly,
    [ValidateSet('26100.8875', '26100.9278')]
    [string]$DwmCoreBuild = '26100.8875'
)

$ErrorActionPreference = 'Stop'
$hookCandidate = $DwmCoreBuild -eq '26100.9278'
if ($hookCandidate) {
    if (-not $OutputDirectory) { throw 'The 9278 candidate requires an explicit fresh OutputDirectory.' }
    $OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
    $artifactPrefix = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot 'artifacts')).TrimEnd('\') + '\'
    if (-not $OutputDirectory.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        $OutputDirectory.Substring($artifactPrefix.Length) -notmatch '^native-9278-candidate-[A-Za-z0-9_-]+(\\.*)?$') {
        throw 'Candidate output must be below artifacts\native-9278-candidate-<name>; production and frozen payload paths are forbidden.'
    }
    if (Test-Path -LiteralPath $OutputDirectory) { throw 'Candidate output must be new; existing artifacts are never overwritten.' }
    for ($ancestor = [IO.DirectoryInfo]::new($OutputDirectory); $null -ne $ancestor; $ancestor = $ancestor.Parent) {
        if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Candidate output may not traverse a reparse point.' }
    }
    if (-not $MinHookLibrary) { throw 'The 9278 candidate requires an explicit isolated MinHookLibrary.' }
}
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $RepositoryRoot 'artifacts\selective-dwm-20260906\v3\bin' }
if (-not $MinHookDirectory) { $MinHookDirectory = Join-Path $RepositoryRoot 'artifacts\deps\minhook-v1.3.4' }
if (-not $MinHookLibrary) { $MinHookLibrary = Join-Path $RepositoryRoot 'artifacts\selective-dwm-20260906\bin\minhook-x64.lib' }
$hookHeader = Join-Path $MinHookDirectory 'include\MinHook.h'
foreach ($requiredFile in @($hookHeader, $MinHookLibrary)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Missing verified MinHook dependency: $requiredFile"
    }
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) { throw 'Visual Studio vswhere is required.' }
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if ($LASTEXITCODE -ne 0 -or -not $installation) { throw 'No Visual Studio x64 C++ toolchain found.' }
$versionFile = Join-Path $installation 'VC\Auxiliary\Build\Microsoft.VCToolsVersion.default.txt'
$compilerVersion = (Get-Content -LiteralPath $versionFile -Raw).Trim()
$compilerRoot = Join-Path $installation "VC\Tools\MSVC\$compilerVersion"
$compiler = Join-Path $compilerRoot 'bin\Hostx64\x64\cl.exe'
$sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10'
$sdkVersion = Get-ChildItem -LiteralPath (Join-Path $sdkRoot 'Include') -Directory |
    Where-Object {
        (Test-Path -LiteralPath (Join-Path $_.FullName 'um\Windows.h')) -and
        (Test-Path -LiteralPath (Join-Path $sdkRoot "Lib\$($_.Name)\um\x64\kernel32.lib"))
    } | Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty Name
if (-not $sdkVersion) { throw 'No complete Windows x64 SDK found.' }

$null = New-Item -ItemType Directory -Path $OutputDirectory -Force
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
$hookObjectDirectory = if ($hookCandidate) { Join-Path $OutputDirectory 'obj\hook' } else { $OutputDirectory }
$null = New-Item -ItemType Directory -Path $hookObjectDirectory -Force
$compileArguments = @('/nologo', '/std:c++17', '/EHsc', '/W4', '/WX', '/O2', '/MT', '/guard:cf')
$compileArguments += '/DHDR_SELECTIVE_DWMCORE_BUILD=' + $DwmCoreBuild.Replace('.', '')
$compileArguments += "/I$(Join-Path $MinHookDirectory 'include')"
$compileArguments += "/I$(Join-Path $compilerRoot 'include')"
foreach ($sdkInclude in @('ucrt', 'shared', 'um')) {
    $compileArguments += "/I$(Join-Path $sdkRoot "Include\$sdkVersion\$sdkInclude")"
}
$linkArguments = @('/link', $MinHookLibrary,
    "/LIBPATH:$(Join-Path $compilerRoot 'lib\x64')",
    "/LIBPATH:$(Join-Path $sdkRoot "Lib\$sdkVersion\ucrt\x64")",
    "/LIBPATH:$(Join-Path $sdkRoot "Lib\$sdkVersion\um\x64")",
    '/DYNAMICBASE', '/NXCOMPAT', '/CETCOMPAT', '/GUARD:CF')

$dll = Join-Path $OutputDirectory 'HDRCalibrate.SelectiveDwmHook.dll'
$dllArguments = $compileArguments + @('/LD', (Join-Path $PSScriptRoot 'SelectiveDwmHook.cpp'),
    "/Fo$(Join-Path $hookObjectDirectory 'HDRCalibrate.SelectiveDwmHook.obj')",
    "/Fd$(Join-Path $hookObjectDirectory 'HDRCalibrate.SelectiveDwmHook.pdb')", "/Fe$dll") + $linkArguments
if (-not $HostTestsOnly) {
    & $compiler @dllArguments
    if ($LASTEXITCODE -ne 0) { throw "Hook build failed: $LASTEXITCODE" }
}

$hostTests = Join-Path $OutputDirectory 'HookSelfTest.exe'
$testArguments = $compileArguments + @('/Ob0', (Join-Path $PSScriptRoot 'HookSelfTest.cpp'),
    "/Fo$(Join-Path $hookObjectDirectory 'HookSelfTest.obj')",
    "/Fd$(Join-Path $hookObjectDirectory 'HookSelfTest.pdb')", "/Fe$hostTests") + $linkArguments
& $compiler @testArguments
if ($LASTEXITCODE -ne 0) { throw "Host test build failed: $LASTEXITCODE" }
if ($RunTests) {
    & $hostTests
    if ($LASTEXITCODE -ne 0) { throw "Host tests failed: $LASTEXITCODE" }
}
if (-not $HostTestsOnly) {
    Get-FileHash -LiteralPath $dll -Algorithm SHA256 | Select-Object Path, Hash
    Write-Output "Built: $dll; private ABI: $DwmCoreBuild (9278 is candidate-only)"
}
Write-Output "Host tests: $hostTests"
