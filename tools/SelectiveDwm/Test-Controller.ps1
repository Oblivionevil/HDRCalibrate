param([string]$BinaryDirectory, [string]$HookPath, [string]$OutputDirectory)

# Parser, local-only export inspection and deliberately invalid selections of
# this test shell. No DWM discovery, target memory access or activation occurs.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (-not $BinaryDirectory) { $BinaryDirectory = Join-Path $taskRoot 'artifacts\program-gamma-native-20260906\bin' }
$controller = (Resolve-Path -LiteralPath (Join-Path $BinaryDirectory 'HDRCalibrate.SelectiveController.exe')).Path
if (-not $HookPath) { $HookPath = Join-Path $BinaryDirectory 'HDRCalibrate.SelectiveDwmHook.dll' }
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
$boundBase = @('--observe', '--dll', $hook, '--target-pid', '1', '--target-hwnd', '2')
$targetBinding = @('--target-exe', 'C:\Programs\Example App.exe', '--target-creation-filetime', '134331673378020485')
$dwmBinding = @('--expected-dwm-pid', '3', '--expected-dwm-creation-filetime', '4')
$pipeName = 'HDRCalibrate.ProgramGamma.0123456789abcdef0123456789abcdef'
foreach ($case in @(
    @{ Name = 'target-exe-without-generation'; Extra = @('--target-exe', 'C:\App.exe') },
    @{ Name = 'target-generation-without-exe'; Extra = @('--target-creation-filetime', '1') },
    @{ Name = 'target-zero-generation'; Extra = @('--target-exe', 'C:\App.exe', '--target-creation-filetime', '0') },
    @{ Name = 'target-negative-generation'; Extra = @('--target-exe', 'C:\App.exe', '--target-creation-filetime', '-1') },
    @{ Name = 'target-overflow-generation'; Extra = @('--target-exe', 'C:\App.exe', '--target-creation-filetime', '9223372036854775808') },
    @{ Name = 'target-duplicate-generation'; Extra = $targetBinding + @('--target-creation-filetime', '3') },
    @{ Name = 'target-duplicate-exe'; Extra = $targetBinding + @('--target-exe', 'C:\Other.exe') },
    @{ Name = 'continuous-without-owner'; Extra = @('--continuous') },
    @{ Name = 'owner-without-time'; Extra = $targetBinding + $dwmBinding + @('--owner-pid', '5', '--pipe', $pipeName) },
    @{ Name = 'owner-without-pid'; Extra = $targetBinding + $dwmBinding + @('--owner-creation-filetime', '6', '--pipe', $pipeName) },
    @{ Name = 'owner-without-pipe'; Extra = $targetBinding + $dwmBinding + @('--owner-pid', '5', '--owner-creation-filetime', '6') },
    @{ Name = 'owner-without-target'; Extra = $dwmBinding + @('--owner-pid', '5', '--owner-creation-filetime', '6', '--pipe', $pipeName) },
    @{ Name = 'owner-without-dwm'; Extra = $targetBinding + @('--owner-pid', '5', '--owner-creation-filetime', '6', '--pipe', $pipeName) },
    @{ Name = 'owner-zero-time'; Extra = $targetBinding + $dwmBinding + @('--owner-pid', '5', '--owner-creation-filetime', '0', '--pipe', $pipeName) },
    @{ Name = 'owner-overflow-time'; Extra = $targetBinding + $dwmBinding + @('--owner-pid', '5', '--owner-creation-filetime', '9223372036854775808', '--pipe', $pipeName) },
    @{ Name = 'pipe-path-not-name'; Extra = $targetBinding + $dwmBinding + @('--owner-pid', '5', '--owner-creation-filetime', '6', '--pipe', ('\\.\pipe\' + $pipeName)) },
    @{ Name = 'pipe-short-nonce'; Extra = $targetBinding + $dwmBinding + @('--owner-pid', '5', '--owner-creation-filetime', '6', '--pipe', 'HDRCalibrate.ProgramGamma.0123') },
    @{ Name = 'pipe-nonhex-nonce'; Extra = $targetBinding + $dwmBinding + @('--owner-pid', '5', '--owner-creation-filetime', '6', '--pipe', 'HDRCalibrate.ProgramGamma.0123456789abcdef0123456789abcdeg') }
)) { $testCases += @{ Name = $case.Name; Args = $boundBase + $case.Extra } }
$badPaths = @('App.exe', 'C:App.exe', '\App.exe', '\\server\share\App.exe', '\\?\C:\App.exe', 'C:\App.exe:stream',
    'C:\a\..\App.exe', 'C:\a\.\App.exe', 'C:\a?\App.exe', 'C:\App.exe.', 'C:\App.exe ', 'C:\\App.exe', 'C:/App.exe')
for ($index = 0; $index -lt $badPaths.Count; $index++) {
    $testCases += @{ Name = "target-noncanonical-path-$index"; Args = $boundBase + @('--target-exe', $badPaths[$index], '--target-creation-filetime', '1') }
}
$testCases += @{ Name = 'inspect-target-binding-conflict'; Args = @('--inspect-dll', '--dll', $hook) + $targetBinding }
$testDirectory = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else {
    Join-Path $taskRoot ('artifacts\program-gamma-native-20260906\controller-tests-' + [Guid]::NewGuid().ToString('N'))
}
New-Item -ItemType Directory -Path $testDirectory | Out-Null
$results = @()
foreach ($test in $testCases) {
    $arguments = @($test.Args)
    if ('--validate-only' -notin $arguments) { $arguments = @('--validate-only') + $arguments }
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
$validRequests += @(
    @{ Name = 'dry-run-exact-target'; Mode = '--veto'; Extra = $targetBinding; ExpectedPid = 0; ExpectedTime = 0L },
    @{ Name = 'dry-run-exact-Unicode-target'; Mode = '--veto'; Extra = @('--target-exe', 'C:\Programme\Über\App.exe', '--target-creation-filetime', '9223372036854775807'); ExpectedPid = 0; ExpectedTime = 0L },
    @{ Name = 'dry-run-managed-continuous'; Mode = '--veto'; Extra = $targetBinding + $dwmBinding + @('--owner-pid', '5', '--owner-creation-filetime', '6', '--pipe', $pipeName, '--continuous'); ExpectedPid = 3; ExpectedTime = 4L }
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
    $exeIndex = [Array]::IndexOf($validArguments, '--target-exe')
    $creationIndex = [Array]::IndexOf($validArguments, '--target-creation-filetime')
    if ($exeIndex -ge 0 -and ($validationEvent.target_exe -cne $validArguments[$exeIndex + 1] -or
        $validationEvent.target_creation_filetime -ne [long]$validArguments[$creationIndex + 1] -or -not $validationEvent.target_identity_explicitly_bound)) {
        throw "Dry-run changed the exact target identity: $($validRequest.Name)"
    }
    if ('--continuous' -in $validArguments -and (-not $validationEvent.continuous -or $validationEvent.owner_pid -ne 5 -or
        $validationEvent.owner_creation_filetime -ne 6 -or $validationEvent.pipe -cne $pipeName)) { throw 'Managed owner contract changed.' }
    $validation | Out-File -LiteralPath (Join-Path $testDirectory ($validRequest.Name + '.log')) -Encoding utf8
    $results += [ordered]@{ Name = $validRequest.Name; ExitCode = $validationExit; Passed = $true }
}
$testShell = [Diagnostics.Process]::GetCurrentProcess()
$shellExe = $testShell.MainModule.FileName
$shellCreation = $testShell.StartTime.ToUniversalTime().ToFileTimeUtc()
foreach ($case in @(
    @{ Name = 'live-metadata-rejects-wrong-generation-before-DWM'; Exe = $shellExe; Creation = ($shellCreation + 1); Exit = 22; Event = 'expected_target_identity_mismatch' },
    @{ Name = 'live-metadata-rejects-wrong-exe-before-DWM'; Exe = ($shellExe + '.different'); Creation = $shellCreation; Exit = 22; Event = 'expected_target_identity_mismatch' },
    @{ Name = 'live-metadata-rejects-wrong-window-before-DWM'; Exe = $shellExe; Creation = $shellCreation; Exit = 12; Event = 'target_window_or_session_mismatch' }
)) {
    $actualArguments = @('--observe', '--dll', $hook, '--target-pid', [string]$testShell.Id, '--target-hwnd', '1',
        '--target-exe', $case.Exe, '--target-creation-filetime', [string]$case.Creation)
    $output = & $controller @actualArguments
    $actualExit = $LASTEXITCODE
    $events = @($output | ForEach-Object { $_ | ConvertFrom-Json })
    $rejection = @($events | Where-Object { $_.event -eq $case.Event })
    if ($actualExit -ne $case.Exit -or $rejection.Count -ne 1 -or $rejection[0].remote_writes -ne 0 -or $rejection[0].remote_calls -ne 0 -or
        @($events | Where-Object { $_.event -notin @('dll_inspected', $case.Event) }).Count) {
        throw "Invalid selection reached an unexpected operation: $($case.Name)"
    }
    $output | Out-File -LiteralPath (Join-Path $testDirectory ($case.Name + '.log')) -Encoding utf8
    $results += [ordered]@{ Name = $case.Name; ExitCode = $actualExit; Passed = $true }
}
$testShell.Dispose()
$report = [ordered]@{ Tests = $results.Count; Passed = $results.Count; LiveDwmAccess = $false; ControllerSha256 = (Get-FileHash -LiteralPath $controller).Hash;
    HookSha256 = (Get-FileHash -LiteralPath $hook).Hash; Results = $results; Output = $testDirectory }
$report | ConvertTo-Json -Depth 6 | Out-File -LiteralPath (Join-Path $testDirectory 'summary.json') -Encoding utf8
$report | ConvertTo-Json -Depth 6
