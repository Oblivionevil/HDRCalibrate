param(
    [string]$ProbePath = (Join-Path $PSScriptRoot '..\..\..\artifacts\selective-dwm-20260906\vrr-policy-sanity\bin\ReadNvidiaVrrPolicy.exe'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\..\..\artifacts\selective-dwm-20260906\vrr-policy-sanity\tests')
)
# This test script never invokes the accepted --exe + absolute-path getter mode.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$ProbePath = (Resolve-Path -LiteralPath $ProbePath).Path
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$policyCases = @(
    @{ Name = 'synthetic'; Arguments = @('--self-test'); Exit = 0 },
    @{ Name = 'relative-path'; Arguments = @('--exe', 'relative.exe'); Exit = 2 },
    @{ Name = 'drive-relative'; Arguments = @('--exe', 'C:relative.exe'); Exit = 2 },
    @{ Name = 'wrong-extension'; Arguments = @('--exe', 'C:\Example\test.dll'); Exit = 2 },
    @{ Name = 'unknown-option'; Arguments = @('--unknown'); Exit = 2 },
    @{ Name = 'extra-argument'; Arguments = @('--self-test', '--extra'); Exit = 2 },
    @{ Name = 'diagnose-alone'; Arguments = @('--diagnose-settings'); Exit = 2 },
    @{ Name = 'diagnose-relative'; Arguments = @('--exe', 'relative.exe', '--diagnose-settings'); Exit = 2 },
    @{ Name = 'diagnose-duplicate'; Arguments = @('--exe', 'C:\Example\test.exe', '--diagnose-settings', '--diagnose-settings'); Exit = 2 },
    @{ Name = 'unknown-extra'; Arguments = @('--exe', 'C:\Example\test.exe', '--unknown'); Exit = 2 }
)
$policyResults = @()
foreach ($policyCase in $policyCases) {
    $policyLog = Join-Path $OutputDirectory ($policyCase.Name + '.jsonl')
    $policyError = Join-Path $OutputDirectory ($policyCase.Name + '.stderr.log')
    $policyArguments = ($policyCase.Arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $policyProcess = Start-Process -FilePath $ProbePath -ArgumentList $policyArguments -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput $policyLog -RedirectStandardError $policyError
    try {
        if (-not $policyProcess.WaitForExit(5000)) { $policyProcess.Kill(); throw 'Own synthetic test exceeded five seconds.' }
        $policyProcess.Refresh()
        $policyEvents = @(Get-Content -LiteralPath $policyLog | ForEach-Object { $_ | ConvertFrom-Json })
        $policyPassed = $policyProcess.ExitCode -eq $policyCase.Exit -and @($policyEvents | Where-Object { $_.event -in @('library', 'config', 'call') }).Count -eq 0
        if ($policyCase.Name -eq 'synthetic') {
            $policyChecks = @($policyEvents | Where-Object event -eq 'self_test_complete')
            $policyPassed = $policyPassed -and $policyChecks.Count -eq 1 -and $policyChecks[0].failures -eq 0 `
                -and $policyChecks[0].driver_queries -eq 0 -and -not $policyChecks[0].driver_loaded
            $policyMissing = @($policyEvents | Where-Object { $_.event -eq 'synthetic_setting' -and $_.case -eq 'missing' })
            $policyPassed = $policyPassed -and $policyMissing.Count -eq 1 -and $null -eq $policyMissing[0].current_value `
                -and $null -eq $policyMissing[0].setting_location -and $policyMissing[0].current_meaning -eq 'unknown'
            $policyNames = @($policyEvents | Where-Object event -eq 'synthetic_setting_name')
            $policyMissingNames = @($policyNames | Where-Object { $_.case -in @('missing', 'api_unavailable') })
            $policyPassed = $policyPassed -and $policyNames.Count -eq 3 -and $policyMissingNames.Count -eq 2 `
                -and @($policyMissingNames | Where-Object { $_.name_known -or $null -ne $_.reported_name }).Count -eq 0
        }
        $policyResults += [ordered]@{ Name = $policyCase.Name; Passed = $policyPassed; ExitCode = $policyProcess.ExitCode; Output = $policyLog }
        Write-Output ('{0}: {1}' -f $policyCase.Name, $(if ($policyPassed) { 'PASS' } else { 'FAIL' }))
    } finally {
        if (-not $policyProcess.HasExited) { $policyProcess.Kill(); [void]$policyProcess.WaitForExit(1000) }
        $policyProcess.Dispose()
    }
}
$policySource = Join-Path $PSScriptRoot 'ReadNvidiaVrrPolicy.cpp'
$policySourceText = Get-Content -Raw -LiteralPath $policySource
$policyForbidden = [regex]::Matches($policySourceText, 'NvAPI_DRS_(Set\w*|Save\w*|CreateProfile|CreateApplication)|OpenProcess|CreateWindow|D3D11CreateDevice')
$policyReport = [ordered]@{
    AllPassed = @($policyResults | Where-Object { -not $_.Passed }).Count -eq 0 -and $policyForbidden.Count -eq 0
    Tests = $policyResults
    ForbiddenSourceMatches = $policyForbidden.Count
    BinarySHA256 = (Get-FileHash -LiteralPath $ProbePath -Algorithm SHA256).Hash
    SourceSHA256 = (Get-FileHash -LiteralPath $policySource -Algorithm SHA256).Hash
    ActualDriverGetterExecuted = $false
    ActualDriverPolicyVerified = $false
}
$policyReport | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'summary.json') -Encoding utf8
Write-Output (Join-Path $OutputDirectory 'summary.json')
if (-not $policyReport.AllPassed) { exit 1 }
