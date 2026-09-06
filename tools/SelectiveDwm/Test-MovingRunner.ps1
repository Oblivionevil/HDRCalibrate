# Extract only the pure result gate by AST. Never execute the live runner body.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$tokens = $null; $parseErrors = $null
$runnerPath = Join-Path $PSScriptRoot 'Run-MovingFixtureExperiment.ps1'
$runnerAst = [System.Management.Automation.Language.Parser]::ParseFile($runnerPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'Moving runner contains PowerShell parse errors.' }
$gates = @($runnerAst.FindAll({ param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Assert-MovingCaptureResult'
}, $false))
if ($gates.Count -ne 1) { throw 'Expected one pure moving-runner result gate.' }
. ([ScriptBlock]::Create($gates[0].Extent.Text))
$cases = @(
    @{ Name = 'valid-with-vrr'; Field = ''; Value = $null; Fails = $false },
    @{ Name = 'controller-failure'; Field = 'ControllerExitCode'; Value = 20; Fails = $true },
    @{ Name = 'presentmon-failure'; Field = 'PresentMonExitCode'; Value = 1; Fails = $true },
    @{ Name = 'fixture-watchdog'; Field = 'FixtureExitCode'; Value = 124; Fails = $true },
    @{ Name = 'fixture-not-completed'; Field = 'FixtureExitCode'; Value = $null; Fails = $true },
    @{ Name = 'vrr-failure'; Field = 'VrrExitCode'; Value = 1; Fails = $true },
    @{ Name = 'vrr-not-completed'; Field = 'VrrExitCode'; Value = $null; Fails = $true },
    @{ Name = 'target-exited-early'; Field = 'TargetIdentityRetained'; Value = $false; Fails = $true },
    @{ Name = 'dwm-restarted'; Field = 'DwmIdentityRetained'; Value = $false; Fails = $true },
    @{ Name = 'valid-no-vrr-request'; Field = 'VrrPolling'; Value = $false; Fails = $false }
)
$results = foreach ($case in $cases) {
    $report = [ordered]@{ ControllerExitCode = 0; PresentMonExitCode = 0; FixtureExitCode = 0;
        VrrPolling = $true; VrrExitCode = 0; TargetIdentityRetained = $true; DwmIdentityRetained = $true }
    if ($case.Field) { $report[$case.Field] = $case.Value }
    if ($case.Name -eq 'valid-no-vrr-request') { $report.VrrExitCode = $null }
    $failed = $false
    try { Assert-MovingCaptureResult $report } catch { $failed = $true }
    if ($failed -ne $case.Fails) { throw "Result gate regression: $($case.Name)" }
    [ordered]@{ Name = $case.Name; Passed = $true }
}
[ordered]@{ Passed = $results.Count; Cases = $results; LiveActivation = $false } | ConvertTo-Json -Depth 4
