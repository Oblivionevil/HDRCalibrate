param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\..\..\artifacts\selective-dwm-20260906\hdr-cycle\bin'))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$cycleSource = Join-Path $PSScriptRoot 'CycleWindowsHdr.cpp'
$cycleVsWhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$cycleVs = & $cycleVsWhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $cycleVs) { throw 'Visual Studio C++ installation unavailable.' }
$cycleDevCmd = Join-Path $cycleVs 'Common7\Tools\VsDevCmd.bat'
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$cycleExe = Join-Path $OutputDirectory 'CycleWindowsHdr.exe'
$cycleObject = Join-Path $OutputDirectory 'CycleWindowsHdr.obj'
foreach ($cyclePath in @($cycleDevCmd, $cycleSource, $cycleExe, $cycleObject)) {
    if ($cyclePath -match '["\r\n%!^&|<>]') { throw 'Unsupported shell character in build path.' }
}
$cycleCommand = '""{0}" -no_logo -arch=x64 -host_arch=x64 -vcvars_ver=14.44 -winsdk=10.0.26100.0 && cl.exe /nologo /std:c++17 /EHsc /W4 /WX /O2 /MT /permissive- /utf-8 /guard:cf /sdl /DUNICODE /D_UNICODE "{1}" /Fo"{2}" /Fe"{3}" /link /DYNAMICBASE /NXCOMPAT user32.lib kernel32.lib"' -f $cycleDevCmd, $cycleSource, $cycleObject, $cycleExe
$cycleStart = [Diagnostics.ProcessStartInfo]::new()
$cycleStart.FileName = $env:ComSpec
$cycleStart.Arguments = '/d /s /c ' + $cycleCommand
$cycleStart.UseShellExecute = $false
$cycleStart.CreateNoWindow = $true
$cycleStart.RedirectStandardOutput = $true
$cycleStart.RedirectStandardError = $true
$cycleBuild = [Diagnostics.Process]::Start($cycleStart)
$cycleStdout = $cycleBuild.StandardOutput.ReadToEndAsync()
$cycleStderr = $cycleBuild.StandardError.ReadToEndAsync()
if (-not $cycleBuild.WaitForExit(60000)) { $cycleBuild.Kill(); throw 'Own build process exceeded 60 seconds.' }
Write-Output $cycleStdout.GetAwaiter().GetResult()
$cycleErrors = $cycleStderr.GetAwaiter().GetResult()
if ($cycleErrors) { Write-Output $cycleErrors }
if ($cycleBuild.ExitCode -ne 0) { throw "HDR cycle compilation failed with exit $($cycleBuild.ExitCode)." }
$cycleBuild.Dispose()
$cycleReport = [ordered]@{
    Path = $cycleExe
    Length = (Get-Item -LiteralPath $cycleExe).Length
    BinarySHA256 = (Get-FileHash -LiteralPath $cycleExe -Algorithm SHA256).Hash
    Source = $cycleSource
    SourceSHA256 = (Get-FileHash -LiteralPath $cycleSource -Algorithm SHA256).Hash
    WindowsSDK = '10.0.26100.0'
    CompilerFlags = '/W4 /WX /O2 /MT /guard:cf /sdl'
    LiveDisplaySettersExecutedByBuild = $false
}
$cycleJson = $cycleReport | ConvertTo-Json -Depth 4
$cycleJson | Set-Content -LiteralPath (Join-Path $OutputDirectory 'build.json') -Encoding utf8
Write-Output $cycleJson
