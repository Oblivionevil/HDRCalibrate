param(
    [string]$BuildDirectory = ""
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$workspace = Split-Path -Parent $PSScriptRoot
$build = if ([string]::IsNullOrWhiteSpace($BuildDirectory)) {
    Join-Path $workspace 'src\HDRCalibrate\bin\x64\Release\net481'
}
else {
    [IO.Path]::GetFullPath($BuildDirectory)
}
$assemblyPath = Join-Path $build 'HDRCalibrate.Dwm.dll'
if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
    throw "Builddatei fehlt: $assemblyPath"
}

[void][Reflection.Assembly]::LoadFrom($assemblyPath)
$sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
function Get-DwmIdentity {
    $process = Get-Process -Name dwm -ErrorAction Stop |
        Where-Object SessionId -eq $sessionId |
        Sort-Object StartTime -Descending |
        Select-Object -First 1
    if ($null -eq $process) { throw 'DWM-Prozess der aktuellen Sitzung fehlt.' }
    try {
        [pscustomobject]@{
            ProcessId = $process.Id
            StartTimeUtcTicks = $process.StartTime.ToUniversalTime().Ticks
        }
    }
    finally {
        $process.Dispose()
    }
}

$before = Get-DwmIdentity
$settings = [HDRCalibrate.Core.CalibrationSettings]::new()
$settings.Gamma = 2.4
$settings.ApplySdrGammaCorrection = $true
$preflight = [HDRCalibrate.Dwm.DwmPatchEngine]::new().Preflight($settings)
$after = Get-DwmIdentity

$result = [pscustomobject]@{
    Success = $preflight.Success
    LivePatchEnabled = -not $preflight.LivePatchBlocked
    TimedTestAvailable = $preflight.TimedTestAvailable
    PatchedShaderCount = $preflight.PatchedShaderCount
    PatchedConstantVectorCount = $preflight.PatchedConstantVectorCount
    ChangedByteCount = $preflight.ChangedByteCount
    DwmUnchanged = $before.ProcessId -eq $after.ProcessId -and
        $before.StartTimeUtcTicks -eq $after.StartTimeUtcTicks
    Message = $preflight.Message
}
$result | ConvertTo-Json

if (-not $result.Success -or -not $result.LivePatchEnabled -or
    -not $result.TimedTestAvailable -or $result.PatchedShaderCount -ne 6 -or
    $result.PatchedConstantVectorCount -ne 24 -or $result.ChangedByteCount -ge 512 -or
    -not $result.DwmUnchanged) {
    throw 'Die fail-closed DWM-Freigabeprüfung ist fehlgeschlagen.'
}
