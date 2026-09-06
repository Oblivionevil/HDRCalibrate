param([string]$BinaryDirectory, [string]$HookPath)

# Parser and local-only export inspection. No valid observe/veto invocation,
# remote process access, injection, or external program launch is performed.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (-not $BinaryDirectory) { $BinaryDirectory = Join-Path $taskRoot 'artifacts\selective-dwm-20260906\controller-generation-bound\bin' }
$controller = (Resolve-Path -LiteralPath (Join-Path $BinaryDirectory 'HDRCalibrate.SelectiveController.exe')).Path
if (-not $HookPath) { $HookPath = Join-Path $taskRoot 'artifacts\selective-dwm-20260906\v3\bin\HDRCalibrate.SelectiveDwmHook.dll' }
$hook = (Resolve-Path -LiteralPath $HookPath).Path
$testCases = @(
    @{ Name = 'missing-mode'; Args = @('--dll', $hook) },
    @{ Name = 'conflicting-modes'; Args = @('--observe', '--veto', '--dll', $hook) },
    @{ Name = 'unknown-option'; Args = @('--inspect-dll', '--dll', $hook, '--all-processes') },
    @{ Name = 'missing-pid'; Args = @('--observe', '--dll', $hook, '--target-hwnd', '1') },
    @{ Name = 'missing-window'; Args = @('--observe', '--dll', $hook, '--target-pid', '1') },
    @{ Name = 'negative-pid'; Args = @('--observe', '--dll', $hook, '--target-pid', '-1', '--target-hwnd', '1') },
    @{ Name = 'zero-pid'; Args = @('--observe', '--dll', $hook, '--target-pid', '0', '--target-hwnd', '1') },
    @{ Name = 'overflow-pid'; Args = @('--observe', '--dll', $hook, '--target-pid', '4294967296', '--target-hwnd', '1') },
    @{ Name = 'overflow-window'; Args = @('--observe', '--dll', $hook, '--target-pid', '1', '--target-hwnd', '18446744073709551616') },
    @{ Name = 'zero-window'; Args = @('--observe', '--dll', $hook, '--target-pid', '1', '--target-hwnd', '0') },
    @{ Name = 'too-long-lease'; Args = @('--inspect-dll', '--dll', $hook, '--duration-ms', '5001') },
    @{ Name = 'too-short-lease'; Args = @('--inspect-dll', '--dll', $hook, '--duration-ms', '99') },
    @{ Name = 'signed-lease'; Args = @('--inspect-dll', '--dll', $hook, '--duration-ms', '+5000') },
    @{ Name = 'duplicate-lease'; Args = @('--inspect-dll', '--dll', $hook, '--duration-ms', '5000', '--duration-ms', '100') },
    @{ Name = 'duplicate-pid'; Args = @('--observe', '--dll', $hook, '--target-pid', '1', '--target-pid', '2', '--target-hwnd', '1') },
    @{ Name = 'missing-option-value'; Args = @('--inspect-dll', '--dll') },
    @{ Name = 'expected-dwm-missing-time'; Args = @('--observe', '--dll', $hook, '--target-pid', '1', '--target-hwnd', '1', '--expected-dwm-pid', '2') },
    @{ Name = 'expected-dwm-missing-pid'; Args = @('--observe', '--dll', $hook, '--target-pid', '1', '--target-hwnd', '1', '--expected-dwm-creation-filetime', '3') },
    @{ Name = 'expected-dwm-inspect-conflict'; Args = @('--inspect-dll', '--dll', $hook, '--expected-dwm-pid', '2', '--expected-dwm-creation-filetime', '3') },
    @{ Name = 'expected-dwm-zero-pid'; Args = @('--observe', '--dll', $hook, '--target-pid', '1', '--target-hwnd', '1', '--expected-dwm-pid', '0', '--expected-dwm-creation-filetime', '3') },
    @{ Name = 'expected-dwm-zero-time'; Args = @('--observe', '--dll', $hook, '--target-pid', '1', '--target-hwnd', '1', '--expected-dwm-pid', '2', '--expected-dwm-creation-filetime', '0') },
    @{ Name = 'expected-dwm-overflow-pid'; Args = @('--observe', '--dll', $hook, '--target-pid', '1', '--target-hwnd', '1', '--expected-dwm-pid', '4294967296', '--expected-dwm-creation-filetime', '3') },
    @{ Name = 'expected-dwm-overflow-time'; Args = @('--observe', '--dll', $hook, '--target-pid', '1', '--target-hwnd', '1', '--expected-dwm-pid', '2', '--expected-dwm-creation-filetime', '9223372036854775808') },
    @{ Name = 'expected-dwm-duplicate-pid'; Args = @('--observe', '--dll', $hook, '--target-pid', '1', '--target-hwnd', '1', '--expected-dwm-pid', '2', '--expected-dwm-pid', '3', '--expected-dwm-creation-filetime', '3') },
    @{ Name = 'expected-dwm-duplicate-time'; Args = @('--observe', '--dll', $hook, '--target-pid', '1', '--target-hwnd', '1', '--expected-dwm-pid', '2', '--expected-dwm-creation-filetime', '3', '--expected-dwm-creation-filetime', '4') },
    @{ Name = 'duplicate-dry-run'; Args = @('--observe', '--dll', $hook, '--target-pid', '1', '--target-hwnd', '1', '--validate-only', '--validate-only') }
    @{ Name = 'leading-whitespace-pid'; Args = @('--observe', '--dll', $hook, '--target-pid', ' 1', '--target-hwnd', '1', '--validate-only') }
    @{ Name = 'leading-whitespace-filetime'; Args = @('--observe', '--dll', $hook, '--target-pid', '1', '--target-hwnd', '1', '--expected-dwm-pid', '2', '--expected-dwm-creation-filetime', ' 3', '--validate-only') }
)
$testDirectory = Join-Path $taskRoot ('artifacts\selective-dwm-20260906\controller-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDirectory | Out-Null
$results = @()
foreach ($test in $testCases) {
    $arguments = @($test.Args)
    $rawOutput = & $controller @arguments 2>&1
    $exitCode = $LASTEXITCODE
    $rawOutput | Out-File -LiteralPath (Join-Path $testDirectory ($test.Name + '.log')) -Encoding utf8
    if ($exitCode -ne 2 -or ($rawOutput -match '"event"')) { throw "Parser test failed: $($test.Name), exit $exitCode." }
    $results += [ordered]@{ Name = $test.Name; ExitCode = $exitCode; Passed = $true }
}
$inspection = & $controller --inspect-dll --dll $hook
$inspectionExit = $LASTEXITCODE
if ($inspectionExit -ne 0) { throw 'Local DLL inspection failed.' }
$inspectionEvent = $inspection | ConvertFrom-Json
if ($inspectionEvent.event -ne 'dll_inspected' -or $inspectionEvent.start_rva -le 0 -or $inspectionEvent.stop_rva -le 0 -or
    $inspectionEvent.caps_rva -le 0 -or $inspectionEvent.protocol_size -ne 480) { throw 'Unexpected local export metadata.' }
$results += [ordered]@{ Name = 'local-only-export-inspection'; ExitCode = $inspectionExit; Passed = $true }
$validRequests = @(
    @{ Name = 'dry-run-unbound-observer'; Mode = '--observe'; Extra = @(); ExpectedPid = 0; ExpectedTime = 0L },
    @{ Name = 'dry-run-bound-veto'; Mode = '--veto'; Extra = @('--expected-dwm-pid', '42', '--expected-dwm-creation-filetime', '134331673378020485'); ExpectedPid = 42; ExpectedTime = 134331673378020485L },
    @{ Name = 'dry-run-bound-max-values'; Mode = '--observe'; Extra = @('--expected-dwm-pid', '4294967295', '--expected-dwm-creation-filetime', '9223372036854775807'); ExpectedPid = 4294967295L; ExpectedTime = 9223372036854775807L }
)
foreach ($validRequest in $validRequests) {
    $validArguments = @($validRequest.Mode, '--dll', $hook, '--target-pid', '1', '--target-hwnd', '2', '--validate-only') + $validRequest.Extra
    $validation = & $controller @validArguments
    $validationExit = $LASTEXITCODE
    if ($validationExit -ne 0) { throw "Valid dry-run request rejected: $($validRequest.Name)" }
    $validationEvent = $validation | ConvertFrom-Json
    if ($validationEvent.event -ne 'request_validated' -or $validationEvent.process_access -or $validationEvent.dll_loaded -or
        $validationEvent.target_pid -ne 1 -or $validationEvent.target_hwnd -ne 2 -or $validationEvent.duration_ms -ne 5000 -or
        $validationEvent.expected_dwm_pid -ne $validRequest.ExpectedPid -or $validationEvent.expected_dwm_creation_filetime -ne $validRequest.ExpectedTime) {
        throw "Dry-run did not retain exact request fields: $($validRequest.Name)"
    }
    $validation | Out-File -LiteralPath (Join-Path $testDirectory ($validRequest.Name + '.log')) -Encoding utf8
    $results += [ordered]@{ Name = $validRequest.Name; ExitCode = $validationExit; Passed = $true }
}
$report = [ordered]@{ Tests = $results.Count; Passed = $results.Count; LiveDwmAccess = $false; ControllerSha256 = (Get-FileHash -LiteralPath $controller).Hash;
    HookSha256 = (Get-FileHash -LiteralPath $hook).Hash; Results = $results; Output = $testDirectory }
$report | ConvertTo-Json -Depth 6 | Out-File -LiteralPath (Join-Path $testDirectory 'summary.json') -Encoding utf8
$report | ConvertTo-Json -Depth 6
