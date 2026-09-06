param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'bin'),
    [string]$VisualStudioPath,
    [string]$ToolsetVersion
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $VisualStudioPath) {
    $fixtureVsWhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $fixtureVsWhere)) { throw 'vswhere.exe is unavailable; supply -VisualStudioPath.' }
    $VisualStudioPath = & $fixtureVsWhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($LASTEXITCODE -ne 0 -or -not $VisualStudioPath) { throw 'No Visual Studio C++ installation found.' }
}
$VisualStudioPath = (Resolve-Path -LiteralPath $VisualStudioPath).Path
$fixtureDevCmd = Join-Path $VisualStudioPath 'Common7\Tools\VsDevCmd.bat'
if (-not (Test-Path -LiteralPath $fixtureDevCmd)) { throw 'VsDevCmd.bat is unavailable.' }
if (-not $ToolsetVersion) {
    $fixtureToolsets = @(Get-ChildItem -LiteralPath (Join-Path $VisualStudioPath 'VC\Tools\MSVC') -Directory | Sort-Object { [version]$_.Name } -Descending)
    $fixturePreferred = @($fixtureToolsets | Where-Object Name -Like '14.44.*')
    $ToolsetVersion = if ($fixturePreferred.Count) { $fixturePreferred[0].Name } else { $fixtureToolsets[0].Name }
}
if ($ToolsetVersion -notmatch '^\d+\.\d+(\.\d+)?$') { throw 'Invalid toolset version.' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$fixtureSource = Join-Path $PSScriptRoot 'ColorFixture.cpp'
$fixtureExe = Join-Path $OutputDirectory 'HDRCalibrate.ColorFixture.exe'
$fixtureObject = Join-Path $OutputDirectory 'ColorFixture.obj'
foreach ($fixturePath in @($fixtureDevCmd, $fixtureSource, $fixtureExe, $fixtureObject)) {
    if ($fixturePath -match '["\r\n%!^&|<>]') { throw 'Unsupported shell character in build path.' }
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$fixtureCommand = '""{0}" -no_logo -arch=x64 -host_arch=x64 -vcvars_ver={1} && cl.exe /nologo /std:c++17 /EHsc /W4 /WX /O2 /MT /permissive- /utf-8 /guard:cf /sdl /DUNICODE /D_UNICODE "{2}" /Fo"{3}" /Fe"{4}" /link /DYNAMICBASE /NXCOMPAT d3d11.lib dxgi.lib user32.lib"' -f $fixtureDevCmd, $ToolsetVersion, $fixtureSource, $fixtureObject, $fixtureExe
$fixtureStart = [Diagnostics.ProcessStartInfo]::new()
$fixtureStart.FileName = $env:ComSpec
$fixtureStart.Arguments = '/d /s /c ' + $fixtureCommand
$fixtureStart.UseShellExecute = $false
$fixtureStart.CreateNoWindow = $true
$fixtureStart.RedirectStandardOutput = $true
$fixtureStart.RedirectStandardError = $true
$fixtureBuild = [Diagnostics.Process]::Start($fixtureStart)
$fixtureStdout = $fixtureBuild.StandardOutput.ReadToEndAsync()
$fixtureStderr = $fixtureBuild.StandardError.ReadToEndAsync()
$fixtureBuild.WaitForExit()
Write-Output $fixtureStdout.GetAwaiter().GetResult()
$fixtureErrors = $fixtureStderr.GetAwaiter().GetResult()
if ($fixtureErrors) { Write-Output $fixtureErrors }
$fixtureExitCode = $fixtureBuild.ExitCode
$fixtureBuild.Dispose()
if ($fixtureExitCode -ne 0) { throw "Fixture compilation failed with exit code $fixtureExitCode." }
$fixtureFile = Get-Item -LiteralPath $fixtureExe
[ordered]@{
    Path = $fixtureFile.FullName
    Length = $fixtureFile.Length
    LastWriteTimeUtc = $fixtureFile.LastWriteTimeUtc.ToString('o')
    ToolsetVersion = $ToolsetVersion
    SHA256 = (Get-FileHash -LiteralPath $fixtureExe -Algorithm SHA256).Hash
} | ConvertTo-Json
