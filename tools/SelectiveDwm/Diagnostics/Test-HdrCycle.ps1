param([string]$Binary = (Join-Path $PSScriptRoot '..\..\..\artifacts\selective-dwm-20260906\hdr-cycle\bin\CycleWindowsHdr.exe'))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$Binary = [IO.Path]::GetFullPath($Binary)
$cycleTestDir = Join-Path (Split-Path -Parent $Binary) ('offline-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $cycleTestDir -Force | Out-Null
$cycleResults = [Collections.Generic.List[object]]::new()
function Run-Case([string]$Name, [string[]]$Arguments, [int]$ExpectedExit, [string]$GuardianEvent = '') {
    $cycleOut = Join-Path $cycleTestDir ($Name + '.jsonl')
    $cycleErr = Join-Path $cycleTestDir ($Name + '.stderr.txt')
    foreach ($cycleArg in @($Binary, $cycleOut, $cycleErr) + $Arguments) {
        if ($cycleArg -match '["\r\n%!^&|<>]') { throw 'Unsupported shell character in synthetic test argument.' }
    }
    $cycleQuoted = ($Arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    # cmd redirection gives a real file handle inherited by the detached helper.
    $cycleCommand = '/d /s /c ""' + $Binary + '" ' + $cycleQuoted + ' >"' + $cycleOut + '" 2>"' + $cycleErr + '""'
    $cycleProcess = Start-Process -FilePath $env:ComSpec -ArgumentList $cycleCommand -WindowStyle Hidden -PassThru
    if (-not $cycleProcess.WaitForExit(15000)) {
        # Only this script's exact owned synthetic controller can be stopped.
        $cycleProcess.Kill()
        throw "Own synthetic controller timed out: $Name"
    }
    $cycleProcess.WaitForExit()
    $cycleExit = $cycleProcess.ExitCode
    $cycleProcess.Dispose()
    $cycleDeadline = [DateTime]::UtcNow.AddSeconds(6)
    do {
        $cycleLines = @(Get-Content -LiteralPath $cycleOut | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json })
        if (-not $GuardianEvent -or @($cycleLines | Where-Object event -eq $GuardianEvent).Count) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $cycleDeadline)
    if ($cycleExit -ne $ExpectedExit) { throw "$Name returned $cycleExit, expected $ExpectedExit. See $cycleOut" }
    if ($GuardianEvent -and -not @($cycleLines | Where-Object event -eq $GuardianEvent).Count) { throw "$Name lacks terminal helper evidence: $cycleOut" }
    if (@($cycleLines | Where-Object { $_.event -eq 'hdr_setter' -and -not $_.simulation }).Count) { throw 'Forbidden live setter in offline test.' }
    $cycleResults.Add([pscustomobject]@{ Case = $Name; ExitCode = $cycleExit; Log = $cycleOut; Events = $cycleLines.Count })
    return ,$cycleLines
}
$cycleSelf = Run-Case 'self-test' @('--self-test') 0
if ($cycleSelf[0].failures -ne 0 -or $cycleSelf[0].display_api_calls -ne 0) { throw 'Pure self-test failure.' }
$null = Run-Case 'invalid-display' @('--cycle', '\\.\DISPLAY2') 2
$null = Run-Case 'invalid-cycle-missing-display' @('--cycle') 2
$null = Run-Case 'invalid-child-handles' @('--restore', '0', '0', '0', '0', '0') 2
$cycleNormal = Run-Case 'normal' @('--simulate', 'normal') 0 'guardian_done'
$cycleNormalOff = @($cycleNormal | Where-Object { $_.event -eq 'hdr_setter' -and -not $_.enable_hdr })
$cycleNormalOn = @($cycleNormal | Where-Object { $_.event -eq 'hdr_setter' -and $_.enable_hdr })
if ($cycleNormalOff.Count -ne 1 -or $cycleNormalOn.Count -ne 1) { throw 'Normal case must execute one simulated OFF and one ON.' }
if (-not @($cycleNormal | Where-Object { $_.event -eq 'cycle_finished' -and $_.off_observed -and $_.restored }).Count) { throw 'Normal restore not verified.' }
$cycleCrash = Run-Case 'exit-after-off' @('--simulate', 'exit-after-off') 25 'guardian_finished'
if (@($cycleCrash | Where-Object { $_.event -eq 'hdr_setter' -and -not $_.enable_hdr }).Count -ne 1) { throw 'Crash test requires exactly one simulated OFF.' }
if (-not @($cycleCrash | Where-Object { $_.event -eq 'guardian_finished' -and $_.restored }).Count) { throw 'Crash recovery failed.' }
if (-not @($cycleCrash | Where-Object { $_.event -eq 'guardian_parent_exited' -and $_.parent_exit_confirmed }).Count) { throw 'Crash recovery lacks confirmed controller exit.' }
$cycleLate = Run-Case 'late-off' @('--simulate', 'late-off') 124 'guardian_finished'
if (@($cycleLate | Where-Object { $_.event -eq 'hdr_setter' -and -not $_.enable_hdr }).Count -ne 0) { throw 'Late OFF race: OFF happened after guardian deadline.' }
if (-not @($cycleLate | Where-Object { $_.event -eq 'guardian_parent_exited' -and $_.parent_exit_confirmed -and $_.forced }).Count) { throw 'Watchdog did not confirm exact controller termination.' }
if (-not @($cycleLate | Where-Object { $_.event -eq 'guardian_finished' -and $_.restored }).Count) { throw 'Late-OFF recovery failed.' }
$cycleReport = [ordered]@{
    Passed = $cycleResults.Count
    PureChecks = $cycleSelf[0].checks
    LiveDisplayApiCalls = 0
    Cases = $cycleResults
    BinarySHA256 = (Get-FileHash -LiteralPath $Binary -Algorithm SHA256).Hash
}
$cycleJson = $cycleReport | ConvertTo-Json -Depth 5
$cycleJson | Set-Content -LiteralPath (Join-Path $cycleTestDir 'test-results.json') -Encoding utf8
Write-Output $cycleJson
