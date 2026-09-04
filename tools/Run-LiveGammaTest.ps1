param(
    [double]$Gamma = 2.4,
    [int]$ActiveMilliseconds = 5000,
    [double]$BrightnessPercent = 0.0,
    [string]$BuildDirectory = ""
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($ActiveMilliseconds -ne 5000) {
    throw 'Der sichere Live-Test besitzt absichtlich eine feste Laufzeit von 5000 Millisekunden.'
}

$arguments = @(
    '-NoProfile',
    '-ExecutionPolicy', 'Bypass',
    '-File', (Join-Path $PSScriptRoot 'Run-SupervisedLiveGammaTest.ps1'),
    '-Gamma', $Gamma.ToString([Globalization.CultureInfo]::InvariantCulture),
    '-BrightnessPercent', $BrightnessPercent.ToString([Globalization.CultureInfo]::InvariantCulture)
)
if (-not [string]::IsNullOrWhiteSpace($BuildDirectory)) {
    $arguments += @('-BuildDirectory', $BuildDirectory)
}

$windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
& $windowsPowerShell @arguments
exit $LASTEXITCODE
