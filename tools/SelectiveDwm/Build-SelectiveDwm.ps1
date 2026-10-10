param(
    [switch]$PreflightOnly,
    [string]$OutputDirectory = '',
    [ValidateSet('26100.8875', '26100.9278')]
    [string]$DwmCoreBuild = '26100.8875'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$selectiveRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$selectiveOutput = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $selectiveRoot 'artifacts\selective-dwm-20260906\bin' }
$selectiveCandidate = $DwmCoreBuild -eq '26100.9278'
if ($selectiveCandidate) {
    if (-not $OutputDirectory) { throw 'The 9278 candidate requires an explicit fresh OutputDirectory.' }
    $artifactPrefix = (Join-Path $selectiveRoot 'artifacts').TrimEnd('\') + '\'
    if (-not $selectiveOutput.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        $selectiveOutput.Substring($artifactPrefix.Length) -notmatch '^native-9278-candidate-[A-Za-z0-9_-]+(\\.*)?$') {
        throw 'Candidate output must be below artifacts\native-9278-candidate-<name>; production and frozen payload paths are forbidden.'
    }
    if (Test-Path -LiteralPath $selectiveOutput) { throw 'Candidate output must be new; existing artifacts are never overwritten.' }
    for ($ancestor = [IO.DirectoryInfo]::new($selectiveOutput); $null -ne $ancestor; $ancestor = $ancestor.Parent) {
        if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Candidate output may not traverse a reparse point.' }
    }
}
$selectiveObject = if ($selectiveCandidate) { Join-Path $selectiveOutput 'obj\controller' } else { Join-Path $selectiveRoot 'artifacts\selective-dwm-20260906\obj' }
$selectiveDefine = '/DHDR_SELECTIVE_DWMCORE_BUILD=' + $DwmCoreBuild.Replace('.', '')
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
    & cl.exe /nologo /W4 /WX /EHsc /std:c++17 /MT /O2 /DUNICODE /D_UNICODE $selectiveDefine (Join-Path $PSScriptRoot 'Controller\Preflight.cpp') "/Fe:$selectiveOutput\HDRCalibrate.SelectivePreflight.exe" /link /DYNAMICBASE /NXCOMPAT
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
    & cl.exe /nologo /W4 /WX /EHsc /std:c++17 /MT /O2 /DUNICODE /D_UNICODE $selectiveDefine (Join-Path $PSScriptRoot 'Controller\Controller.cpp') "/Fe:$selectiveOutput\HDRCalibrate.SelectiveController.exe" /link /DYNAMICBASE /NXCOMPAT user32.lib
    if ($LASTEXITCODE -ne 0) { throw 'Controller build failed.' }
    Write-Output "Build output: $selectiveOutput; private ABI: $DwmCoreBuild (9278 is candidate-only)"
} finally { Pop-Location }
