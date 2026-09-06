[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..\..')).Path,
    [string]$OutputDirectory = '',
    [string]$MinHookDirectory = '',
    [string]$MinHookLibrary = '',
    [switch]$RunTests,
    [switch]$HostTestsOnly
)

$ErrorActionPreference = 'Stop'
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
$compileArguments = @('/nologo', '/std:c++17', '/EHsc', '/W4', '/WX', '/O2', '/MT', '/guard:cf')
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
    "/Fo$(Join-Path $OutputDirectory 'HDRCalibrate.SelectiveDwmHook.obj')",
    "/Fd$(Join-Path $OutputDirectory 'HDRCalibrate.SelectiveDwmHook.pdb')", "/Fe$dll") + $linkArguments
if (-not $HostTestsOnly) {
    & $compiler @dllArguments
    if ($LASTEXITCODE -ne 0) { throw "Hook build failed: $LASTEXITCODE" }
}

$hostTests = Join-Path $OutputDirectory 'HookSelfTest.exe'
$testArguments = $compileArguments + @('/Ob0', (Join-Path $PSScriptRoot 'HookSelfTest.cpp'),
    "/Fo$(Join-Path $OutputDirectory 'HookSelfTest.obj')",
    "/Fd$(Join-Path $OutputDirectory 'HookSelfTest.pdb')", "/Fe$hostTests") + $linkArguments
& $compiler @testArguments
if ($LASTEXITCODE -ne 0) { throw "Host test build failed: $LASTEXITCODE" }
if ($RunTests) {
    & $hostTests
    if ($LASTEXITCODE -ne 0) { throw "Host tests failed: $LASTEXITCODE" }
}
Get-FileHash -LiteralPath $dll -Algorithm SHA256 | Select-Object Path, Hash
Write-Output "Built: $dll"
Write-Output "Host tests: $hostTests"
