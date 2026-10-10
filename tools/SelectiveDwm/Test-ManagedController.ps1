#Requires -Version 7.0
[CmdletBinding()]
param([string]$BinaryDirectory = '', [string]$OutputDirectory = '')
# Real standalone controller + local .NET pipe. Every case either sends STOP
# before GO or selects invalid self-shell metadata/HWND. Expected DWM PID 1 is
# an additional rejection guard. No DWM discovery/preflight/activation occurs.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (-not $BinaryDirectory) { $BinaryDirectory = Join-Path $repository 'artifacts\program-gamma-native-20260906\bin' }
$controller = (Resolve-Path -LiteralPath (Join-Path $BinaryDirectory 'HDRCalibrate.SelectiveController.exe')).Path
$hook = (Resolve-Path -LiteralPath (Join-Path $BinaryDirectory 'HDRCalibrate.SelectiveDwmHook.dll')).Path
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repository ('artifacts\program-gamma-native-20260906\managed-tests-' + [Guid]::NewGuid().ToString('N')) }
$null = New-Item -ItemType Directory -Path $OutputDirectory
$shellProcess = [Diagnostics.Process]::GetCurrentProcess()
$shellExe = $shellProcess.MainModule.FileName
$shellCreation = $shellProcess.StartTime.ToUniversalTime().ToFileTimeUtc()
$results = @()
foreach ($case in @(
    @{ Name = 'STOP-before-GO'; Command = 2; Creation = $shellCreation; Exit = 23; RejectEvent = 'managed_start_cancelled' },
    @{ Name = 'GO-wrong-generation'; Command = 3; Creation = ($shellCreation + 1); Exit = 22; RejectEvent = 'expected_target_identity_mismatch' },
    @{ Name = 'GO-wrong-window'; Command = 3; Creation = $shellCreation; Exit = 12; RejectEvent = 'target_window_or_session_mismatch' }
)) {
    $name = 'HDRCalibrate.ProgramGamma.' + [Guid]::NewGuid().ToString('N')
    $pipe = [IO.Pipes.NamedPipeServerStream]::new($name, [IO.Pipes.PipeDirection]::InOut, 1, [IO.Pipes.PipeTransmissionMode]::Message, [IO.Pipes.PipeOptions]::Asynchronous)
    $process = [Diagnostics.Process]::new()
    $reader = $null
    try {
        $nativeArgs = @('--observe', '--dll', $hook, '--target-pid', [string]$shellProcess.Id, '--target-hwnd', '1',
            '--target-exe', $shellExe, '--target-creation-filetime', [string]$case.Creation,
            '--expected-dwm-pid', '1', '--expected-dwm-creation-filetime', '1', '--owner-pid', [string]$shellProcess.Id,
            '--owner-creation-filetime', [string]$shellCreation, '--pipe', $name, '--continuous')
        $process.StartInfo.FileName = $controller
        $process.StartInfo.UseShellExecute = $false
        $process.StartInfo.CreateNoWindow = $true
        $process.StartInfo.RedirectStandardOutput = $true
        $process.StartInfo.RedirectStandardError = $true
        foreach ($argument in $nativeArgs) { $process.StartInfo.ArgumentList.Add($argument) }
        $connection = $pipe.WaitForConnectionAsync()
        if (-not $process.Start()) { throw 'Controller did not start.' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $connection.Wait(5000)) { throw 'Controller did not connect.' }
        $connection.GetAwaiter().GetResult()
        $reader = [IO.StreamReader]::new($pipe, [Text.Encoding]::UTF8, $false, 4096, $true)
        $events = @()
        $lineTask = $reader.ReadLineAsync()
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $sent = $false
        $lastHeartbeat = 0L
        $ended = $false
        while ($watch.ElapsedMilliseconds -lt 8000) {
            if ($lineTask.IsCompleted) {
                $line = $lineTask.GetAwaiter().GetResult()
                if ($null -eq $line) { $ended = $true; break }
                $event = $line | ConvertFrom-Json
                $events += $event
                if (-not $sent) {
                    if ($event.event -ne 'managed_connected' -or $event.pid -ne $process.Id -or $event.owner_pid -ne $shellProcess.Id -or -not $event.continuous) {
                        throw 'Exact controller/owner handshake was not retained.'
                    }
                    $pipe.WriteByte([byte]$case.Command)
                    $pipe.Flush()
                    $sent = $true
                    $lastHeartbeat = $watch.ElapsedMilliseconds
                }
                $lineTask = $reader.ReadLineAsync()
            }
            if ($sent -and $case.Command -eq 3 -and -not $process.HasExited -and $watch.ElapsedMilliseconds - $lastHeartbeat -ge 250) {
                $pipe.WriteByte(1)
                $pipe.Flush()
                $lastHeartbeat = $watch.ElapsedMilliseconds
            }
            Start-Sleep -Milliseconds 10
        }
        if (-not $ended -or -not $process.WaitForExit(2000)) { throw 'Controller did not finish its bounded negative request.' }
        $capturedStdout = $stdout.GetAwaiter().GetResult()
        $capturedStderr = $stderr.GetAwaiter().GetResult()
        $rejected = @($events | Where-Object { $_.event -eq $case.RejectEvent })
        $allowed = @('managed_connected', 'managed_go', 'dll_inspected', $case.RejectEvent)
        if ($process.ExitCode -ne $case.Exit -or $rejected.Count -ne 1 -or $rejected[0].remote_writes -ne 0 -or $rejected[0].remote_calls -ne 0 -or
            @($events | Where-Object { $_.event -notin $allowed }).Count -or $capturedStdout -or $capturedStderr) {
            throw "Managed negative request violated the pipe-only rejection contract: $($case.Name)"
        }
        $events | ConvertTo-Json -Depth 5 | Out-File -LiteralPath (Join-Path $OutputDirectory ($case.Name + '.json')) -Encoding utf8
        $results += [ordered]@{ Name = $case.Name; ExitCode = $process.ExitCode; Passed = $true; Events = @($events | ForEach-Object { $_.event }) }
    } finally {
        if ($pipe.IsConnected -and -not $process.HasExited) {
            try { $pipe.WriteByte(2); $pipe.Flush() } catch { }
        }
        if ($reader) { $reader.Dispose() }
        $pipe.Dispose()
        if ($process.Id -and -not $process.HasExited) { $null = $process.WaitForExit(3000) }
        $process.Dispose()
    }
}
$shellProcess.Dispose()
$report = [ordered]@{ Tests = $results.Count; Passed = $results.Count; LiveDwmAccess = $false; ControllerSha256 = (Get-FileHash -LiteralPath $controller).Hash; Results = $results; Output = $OutputDirectory }
$report | ConvertTo-Json -Depth 6 | Out-File -LiteralPath (Join-Path $OutputDirectory 'summary.json') -Encoding utf8
$report | ConvertTo-Json -Depth 6
