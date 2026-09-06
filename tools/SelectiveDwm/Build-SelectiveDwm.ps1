param([switch]$PreflightOnly, [string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$selectiveRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$selectiveOutput = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $selectiveRoot 'artifacts\selective-dwm-20260906\bin' }
$selectiveObject = Join-Path $selectiveRoot 'artifacts\selective-dwm-20260906\obj'
$selectiveDependency = Join-Path $selectiveRoot 'artifacts\deps\minhook-v1.3.4'
$selectiveVswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$selectiveVs = & $selectiveVswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $selectiveVs) { throw 'MSVC x64 compiler not found.' }
$selectiveDevCmd = Join-Path $selectiveVs 'Common7\Tools\VsDevCmd.bat'
foreach ($selectiveBuildPath in @($selectiveOutput, $selectiveObject)) {
    New-Item -ItemType Directory -Path $selectiveBuildPath -Force | Out-Null
}
# Import the compiler environment without opening a visible helper window.
$selectiveEnvCommand = 'call "' + $selectiveDevCmd + '" -no_logo -host_arch=x64 -arch=x64 >nul && set'
$selectiveEnvLines = & $env:ComSpec /d /c $selectiveEnvCommand
if ($LASTEXITCODE -ne 0) { throw 'Failed to initialize MSVC.' }
foreach ($selectiveLine in $selectiveEnvLines) {
    if ($selectiveLine -match '^([^=]+)=(.*)$') { [Environment]::SetEnvironmentVariable($matches[1], $matches[2], 'Process') }
}
Push-Location $selectiveObject
try {
    & cl.exe /nologo /W4 /WX /EHsc /std:c++17 /MT /O2 /DUNICODE /D_UNICODE (Join-Path $PSScriptRoot 'Controller\Preflight.cpp') "/Fe:$selectiveOutput\HDRCalibrate.SelectivePreflight.exe" /link /DYNAMICBASE /NXCOMPAT
    if ($LASTEXITCODE -ne 0) { throw 'Preflight build failed.' }
    if ($PreflightOnly) { return }
    $selectivePin = (& git -C $selectiveDependency rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $selectivePin -ne 'c3fcafdc10146beb5919319d0683e44e3c30d537') {
        throw 'MinHook v1.3.4 source is missing or not at the reviewed revision.'
    }
    $selectiveDependencyChanges = & git -C $selectiveDependency status --porcelain --untracked-files=all
    if ($LASTEXITCODE -ne 0 -or $selectiveDependencyChanges) { throw 'MinHook source has local modifications.' }
    foreach ($selectiveSource in @('buffer.c', 'hook.c', 'trampoline.c', 'hde\hde64.c')) {
        & cl.exe /nologo /W3 /MT /O2 /c (Join-Path $selectiveDependency "src\$selectiveSource")
        if ($LASTEXITCODE -ne 0) { throw "MinHook compilation failed: $selectiveSource" }
    }
    & lib.exe /nologo "/OUT:$selectiveOutput\minhook-x64.lib" buffer.obj hook.obj trampoline.obj hde64.obj
    if ($LASTEXITCODE -ne 0) { throw 'MinHook archive failed.' }
    Copy-Item -LiteralPath (Join-Path $selectiveDependency 'LICENSE.txt') -Destination (Join-Path $selectiveOutput 'MinHook-LICENSE.txt')
    & cl.exe /nologo /W4 /WX /EHsc /std:c++17 /MT /O2 /DUNICODE /D_UNICODE (Join-Path $PSScriptRoot 'Controller\Controller.cpp') "/Fe:$selectiveOutput\HDRCalibrate.SelectiveController.exe" /link /DYNAMICBASE /NXCOMPAT user32.lib
    if ($LASTEXITCODE -ne 0) { throw 'Controller build failed.' }
    Write-Output "Build output: $selectiveOutput"
} finally { Pop-Location }
