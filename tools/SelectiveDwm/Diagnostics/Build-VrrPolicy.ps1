param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\..\..\artifacts\selective-dwm-20260906\vrr-policy-sanity\bin'))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$policyPin = 'a08f45f737bb142b070f1e23c58466ecbc82e398'
$policyDeps = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('..\..\..\artifacts\selective-dwm-20260906\vrr-policy\deps\' + $policyPin)))
$policyHashes = [ordered]@{
    'nvapi.h' = '76BBB71107C1388134D41712BED56BB6D80EC426F9BB2C168E2D6941B90F16CA'
    'NvApiDriverSettings.h' = '95FFDA903832840343A81189E052A0756774BD92E4B37F2DC49598D493F951D4'
    'nvapi_interface.h' = 'D279F81E7F92F2743BFEA2EC7ADA693001805E13645EB1C6F2AF44EF9C342834'
    'nvapi_lite_common.h' = '3904BAAB8E8501A53746DDF4E5B13A2E1D5ACD6EE50F8C3C2EADD8E4D62C55F2'
    'nvapi_lite_d3dext.h' = '2322DB28A2F7384FF6DF6FD1110B48E99660FA29956A2CBCD28A9D7157017E1A'
    'nvapi_lite_salend.h' = '92017F8E26F585601F4EAAC475FA9E542EAA2F99A80E376DDE52F2F73AB18E67'
    'nvapi_lite_salstart.h' = '9DB770E52947B33D68B0C1F17BC21EDD9D736B824CCECEE2C5C527D48A89BE93'
    'nvapi_lite_sli.h' = '2A9C75FAECB32BC7A5237A4B83CC28A6A488C75493E44BF28688ABF3FEC01A34'
    'nvapi_lite_stereo.h' = 'BD6FE69136CFF36C73A9967A7CEF37186791D9B7C3EB3AE7AC61B4EFA628325B'
    'nvapi_lite_surround.h' = '197AD1E4762B2243904B0A13418747436AE70E4824099BF56BE4804C4C29956F'
    'License.txt' = 'BAB44777F0F4D0333479EA0BE6E24C084464A8CB64B98F1253FCC3DF70F6ECCF'
}
New-Item -ItemType Directory -Path $policyDeps -Force | Out-Null
foreach ($policyFile in $policyHashes.Keys) {
    $policyPath = Join-Path $policyDeps $policyFile
    if (-not (Test-Path -LiteralPath $policyPath)) {
        Invoke-WebRequest -Uri ('https://raw.githubusercontent.com/NVIDIA/nvapi/' + $policyPin + '/' + $policyFile) -OutFile $policyPath
    }
    if ((Get-FileHash -LiteralPath $policyPath -Algorithm SHA256).Hash -ne $policyHashes[$policyFile]) { throw "Pinned SDK checksum mismatch: $policyFile" }
}
# Verify every dynamically resolved public API ID against the pinned interface.
$policySource = Join-Path $PSScriptRoot 'ReadNvidiaVrrPolicy.cpp'
$policyText = Get-Content -Raw -LiteralPath $policySource
$policyInterface = Get-Content -Raw -LiteralPath (Join-Path $policyDeps 'nvapi_interface.h')
$policyResolutions = [regex]::Matches($policyText, 'Resolve\(\w+,(0x[0-9a-f]+),"(NvAPI_[^"]+)"(?:,true)?\)')
if ($policyResolutions.Count -ne 11) { throw 'Expected ten normal and one optional public API resolutions.' }
foreach ($policyResolution in $policyResolutions) {
    $policyExpected = '\{\s*"' + [regex]::Escape($policyResolution.Groups[2].Value) + '"\s*,\s*' + $policyResolution.Groups[1].Value + '\s*\}'
    if ($policyInterface -notmatch $policyExpected) { throw 'Resolved API ID is not in the pinned public SDK.' }
}
$policyVsWhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$policyVs = & $policyVsWhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $policyVs) { throw 'Visual Studio C++ installation unavailable.' }
$policyDevCmd = Join-Path $policyVs 'Common7\Tools\VsDevCmd.bat'
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$policyExe = Join-Path $OutputDirectory 'ReadNvidiaVrrPolicy.exe'
$policyObject = Join-Path $OutputDirectory 'ReadNvidiaVrrPolicy.obj'
foreach ($policyPath in @($policyDevCmd, $policySource, $policyExe, $policyObject, $policyDeps)) {
    if ($policyPath -match '["\r\n%!^&|<>]') { throw 'Unsupported shell character in build path.' }
}
$policyCommand = '""{0}" -no_logo -arch=x64 -host_arch=x64 -vcvars_ver=14.44 && cl.exe /nologo /std:c++17 /EHsc /W4 /WX /O2 /MT /permissive- /utf-8 /guard:cf /sdl /DUNICODE /D_UNICODE /external:I"{1}" /external:W0 "{2}" /Fo"{3}" /Fe"{4}" /link /DYNAMICBASE /NXCOMPAT kernel32.lib"' -f $policyDevCmd, $policyDeps, $policySource, $policyObject, $policyExe
$policyStart = [Diagnostics.ProcessStartInfo]::new()
$policyStart.FileName = $env:ComSpec
$policyStart.Arguments = '/d /s /c ' + $policyCommand
$policyStart.UseShellExecute = $false
$policyStart.CreateNoWindow = $true
$policyStart.RedirectStandardOutput = $true
$policyStart.RedirectStandardError = $true
$policyBuild = [Diagnostics.Process]::Start($policyStart)
$policyStdout = $policyBuild.StandardOutput.ReadToEndAsync()
$policyStderr = $policyBuild.StandardError.ReadToEndAsync()
if (-not $policyBuild.WaitForExit(60000)) { $policyBuild.Kill(); throw 'Own build process exceeded 60 seconds.' }
Write-Output $policyStdout.GetAwaiter().GetResult()
$policyErrors = $policyStderr.GetAwaiter().GetResult()
if ($policyErrors) { Write-Output $policyErrors }
if ($policyBuild.ExitCode -ne 0) { throw "Policy compilation failed with exit $($policyBuild.ExitCode)." }
$policyBuild.Dispose()
Copy-Item -LiteralPath (Join-Path $policyDeps 'License.txt') -Destination (Join-Path $OutputDirectory 'NVAPI-SDK-LICENSE.txt')
$policyReport = [ordered]@{
    Path = $policyExe
    Length = (Get-Item -LiteralPath $policyExe).Length
    BinarySHA256 = (Get-FileHash -LiteralPath $policyExe -Algorithm SHA256).Hash
    Source = $policySource
    SourceSHA256 = (Get-FileHash -LiteralPath $policySource -Algorithm SHA256).Hash
    SdkCommit = $policyPin
    SdkSHA256 = $policyHashes
    VerifiedPublicApiIds = $policyResolutions.Count
    DriverGetterExecuted = $false
}
$policyJson = $policyReport | ConvertTo-Json -Depth 5
$policyJson | Set-Content -LiteralPath (Join-Path $OutputDirectory 'build.json') -Encoding utf8
Write-Output $policyJson
