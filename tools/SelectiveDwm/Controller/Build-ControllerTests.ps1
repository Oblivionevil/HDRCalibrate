[CmdletBinding()]
param(
    [string]$OutputDirectory = '',
    [string]$HookPath = '',
    [switch]$RunTests,
    [ValidateSet('26100.8875', '26100.9278')]
    [string]$DwmCoreBuild = '26100.8875'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$controllerCandidate = $DwmCoreBuild -eq '26100.9278'
if ($controllerCandidate) {
    if (-not $OutputDirectory) { throw 'The 9278 candidate requires an explicit fresh OutputDirectory.' }
    $OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
    $artifactPrefix = (Join-Path $repository 'artifacts').TrimEnd('\') + '\'
    if (-not $OutputDirectory.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        $OutputDirectory.Substring($artifactPrefix.Length) -notmatch '^native-9278-candidate-[A-Za-z0-9_-]+(\\.*)?$') {
        throw 'Candidate output must be below artifacts\native-9278-candidate-<name>; production and frozen payload paths are forbidden.'
    }
    if (Test-Path -LiteralPath $OutputDirectory) { throw 'Candidate output must be new; existing artifacts are never overwritten.' }
    for ($ancestor = [IO.DirectoryInfo]::new($OutputDirectory); $null -ne $ancestor; $ancestor = $ancestor.Parent) {
        if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Candidate output may not traverse a reparse point.' }
    }
    if ($RunTests -and -not $HookPath) { throw 'Candidate host tests require the explicit isolated HookPath.' }
}
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repository 'artifacts\program-gamma-native-20260906\bin' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$protectedDirectory = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts\selective-dwm-20260906\v3\bin'))
if ($OutputDirectory.Equals($protectedDirectory, [StringComparison]::OrdinalIgnoreCase)) { throw 'The currently mapped hook directory is not a test output.' }
if (-not $HookPath) { $HookPath = Join-Path $OutputDirectory 'HDRCalibrate.SelectiveDwmHook.dll' }
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if ($LASTEXITCODE -ne 0 -or -not $installation) { throw 'No MSVC x64 toolchain found.' }
$version = (Get-Content -LiteralPath (Join-Path $installation 'VC\Auxiliary\Build\Microsoft.VCToolsVersion.default.txt') -Raw).Trim()
$compilerRoot = Join-Path $installation "VC\Tools\MSVC\$version"
$compiler = Join-Path $compilerRoot 'bin\Hostx64\x64\cl.exe'
$sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10'
$sdkVersion = Get-ChildItem -LiteralPath (Join-Path $sdkRoot 'Include') -Directory | Where-Object {
    (Test-Path -LiteralPath (Join-Path $_.FullName 'um\Windows.h')) -and
    (Test-Path -LiteralPath (Join-Path $sdkRoot "Lib\$($_.Name)\um\x64\kernel32.lib"))
} | Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty Name
if (-not $sdkVersion) { throw 'No complete Windows SDK found.' }
$null = New-Item -ItemType Directory -Path $OutputDirectory -Force
$controllerObjectDirectory = if ($controllerCandidate) { Join-Path $OutputDirectory 'obj\controller-tests' } else { $OutputDirectory }
$null = New-Item -ItemType Directory -Path $controllerObjectDirectory -Force
$compile = @('/nologo', '/std:c++17', '/EHsc', '/W4', '/WX', '/O2', '/MT', '/guard:cf', '/DUNICODE', '/D_UNICODE', "/I$(Join-Path $compilerRoot 'include')")
$compile += '/DHDR_SELECTIVE_DWMCORE_BUILD=' + $DwmCoreBuild.Replace('.', '')
foreach ($include in @('ucrt', 'shared', 'um')) { $compile += "/I$(Join-Path $sdkRoot "Include\$sdkVersion\$include")" }
$link = @('/link', 'user32.lib', "/LIBPATH:$(Join-Path $compilerRoot 'lib\x64')",
    "/LIBPATH:$(Join-Path $sdkRoot "Lib\$sdkVersion\ucrt\x64")", "/LIBPATH:$(Join-Path $sdkRoot "Lib\$sdkVersion\um\x64")",
    '/DYNAMICBASE', '/NXCOMPAT', '/CETCOMPAT', '/GUARD:CF')
foreach ($item in @(@{ Source = 'Controller.cpp'; Output = 'HDRCalibrate.SelectiveController' }, @{ Source = 'ControllerSelfTest.cpp'; Output = 'ControllerSelfTest' })) {
    $arguments = $compile + @((Join-Path $PSScriptRoot $item.Source), "/Fo$(Join-Path $controllerObjectDirectory ($item.Output + '.obj'))",
        "/Fd$(Join-Path $controllerObjectDirectory ($item.Output + '.pdb'))", "/Fe$(Join-Path $OutputDirectory ($item.Output + '.exe'))") + $link
    & $compiler @arguments
    if ($LASTEXITCODE -ne 0) { throw "Native controller build failed: $($item.Source)" }
}
if ($RunTests) {
    if (-not (Test-Path -LiteralPath $HookPath -PathType Leaf)) { throw 'Build the isolated hook test artifact before host tests.' }
    # Module enumeration returns canonical absolute paths. Keep a caller's
    # relative test input from looking like a conflicting second DLL path.
    $HookPath = (Resolve-Path -LiteralPath $HookPath).Path
    & (Join-Path $OutputDirectory 'ControllerSelfTest.exe') $HookPath |
        Tee-Object -FilePath (Join-Path $OutputDirectory 'controller-host-tests.log')
    if ($LASTEXITCODE -ne 0) { throw 'Native controller host tests failed.' }
}
Get-FileHash -LiteralPath (Join-Path $OutputDirectory 'HDRCalibrate.SelectiveController.exe') -Algorithm SHA256 | Select-Object Path, Hash
