using Microsoft.Win32.SafeHandles;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace HDRCalibrate.Services
{
    /// <summary>One owned helper and one bounded lease session; no app launch or process kill.</summary>
    public sealed class ProgramGammaSessionRunner : IProgramGammaSessionRunner
    {
        private const int ApprovalRevalidationMilliseconds = 1000;
        private readonly Dispatcher _ownerDispatcher = Dispatcher.FromThread(Thread.CurrentThread);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

        public async Task<ProgramGammaRunResult> RunAsync(ProgramGammaTarget target, ProgramGammaApproval approval,
            bool continuous, Func<bool> authorizeStart, Action<ProgramGammaNativeEvent> onEvent, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (approval == null) throw new InvalidOperationException("Keine gültige aktive SDR-Gamma-Freigabe.");
            ProgramGammaBuildSupport.RequireSupported(approval.DwmCoreVersion, approval.WindowsEnvironment.DwmCoreSha256);
            using (var files = ProgramGammaNativeFiles.OpenVerified())
            using (var own = Process.GetCurrentProcess())
            {
                var pipeName = "HDRCalibrate.ProgramGamma." + Guid.NewGuid().ToString("N");
                var arguments = ProgramGammaArguments.Build(target, approval.DwmProcessId, approval.DwmCreationFileTime,
                    own.Id, own.StartTime.ToUniversalTime().ToFileTimeUtc(), files.HookPath, pipeName, continuous);
                using (var pipe = CreatePipe(pipeName))
                {
                    // UAC is requested only after an explicit Test/Activate click. An
                    // outstanding UAC prompt never grants GO if the dialog was closed.
                    using (var helper = await LaunchHelperAsync(new ProcessStartInfo
                    {
                        FileName = files.ControllerPath,
                        Arguments = arguments,
                        WorkingDirectory = Path.GetDirectoryName(files.ControllerPath),
                        UseShellExecute = true,
                        Verb = "runas",
                        WindowStyle = ProcessWindowStyle.Hidden
                    }, cancellationToken).ConfigureAwait(false))
                    {
                        if (helper == null) throw new InvalidOperationException("Der Gamma-Helfer konnte nicht gestartet werden.");
                        // Retain this exact process object/handle, not a later PID lookup.
                        var retainedHandle = helper.Handle;
                        cancellationToken.ThrowIfCancellationRequested();
                        await ConnectAsync(pipe, helper, cancellationToken).ConfigureAwait(false);
                        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientPid)
                            || clientPid != (uint)helper.Id || helper.HasExited)
                            throw new InvalidOperationException("Die Gamma-Helferidentität am Kanal stimmt nicht überein.");

                        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                        {
                            var sawActive = 0;
                            var read = ReadEventsAsync(pipe, item =>
                            {
                                if (item.ConfirmsActiveSelectionFor(target)) Interlocked.Exchange(ref sawActive, 1);
                                onEvent(item);
                            });
                            ObserveFailure(read);
                            Task<HeartbeatResult> heartbeat = null;
                            try
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                if (!authorizeStart() || !approval.IsStillValid() || helper.HasExited)
                                    throw new InvalidOperationException("Die Freigabe oder das Programmfenster hat sich während der UAC-Abfrage geändert.");
                                cancellationToken.ThrowIfCancellationRequested();
                                await SendByteAsync(pipe, 3).ConfigureAwait(false);
                                heartbeat = HeartbeatAsync(pipe, approval, stop.Token);
                                var elapsed = Stopwatch.StartNew();
                                long? stoppingAt = null;
                                string failure = null;
                                // The reader runs continuously so an elevated helper can
                                // never block lease withdrawal behind an unread pipe.
                                while (!helper.HasExited)
                                {
                                    if (read.IsCompleted && read.Status == TaskStatus.RanToCompletion
                                        && read.Result.Completion == null)
                                        failure = read.Result.Failure ?? "Der Gamma-Kanal wurde ohne Abschlussnachweis geschlossen.";
                                    if (read.IsFaulted) failure = "Ungültige Gamma-Helferantwort: " + read.Exception.GetBaseException().Message;
                                    if (continuous && elapsed.ElapsedMilliseconds > 30000 && Volatile.Read(ref sawActive) == 0)
                                    {
                                        failure = "Innerhalb von 30 Sekunden kein bestätigter SDR-Auswahlpfad. Erzwingen gestoppt.";
                                        stop.Cancel();
                                    }
                                    if (heartbeat.IsCompleted || read.IsCompleted || stop.IsCancellationRequested
                                        || (!continuous && elapsed.ElapsedMilliseconds > 25000))
                                    {
                                        if (!stoppingAt.HasValue)
                                        {
                                            stoppingAt = elapsed.ElapsedMilliseconds;
                                            stop.Cancel();
                                        }
                                    }
                                    if (stoppingAt.HasValue && elapsed.ElapsedMilliseconds - stoppingAt.Value > 12000)
                                    {
                                        pipe.Dispose();
                                        return new ProgramGammaRunResult { ExitCode = -1,
                                            Failure = "Der Abschluss des Helfers wurde nicht bestätigt. Keine weitere Lease-Verlängerung; kein automatischer DWM-Neustart." };
                                    }
                                    await Task.Delay(50).ConfigureAwait(false);
                                }
                                stop.Cancel();
                                await Task.WhenAny(read, Task.Delay(1000)).ConfigureAwait(false);
                                var outcome = read.Status == TaskStatus.RanToCompletion ? read.Result : null;
                                if (heartbeat.Status == TaskStatus.RanToCompletion && !string.IsNullOrEmpty(heartbeat.Result.Failure)
                                    && (heartbeat.Result.AuthorizationLost
                                        || outcome?.Completion?.ConfirmsSafeCompletion(helper.ExitCode) != true))
                                    failure = failure ?? outcome?.Failure ?? heartbeat.Result.Failure;
                                return new ProgramGammaRunResult { ExitCode = helper.ExitCode, Completion = outcome?.Completion,
                                    Failure = failure ?? outcome?.Failure };
                            }
                            finally
                            {
                                stop.Cancel();
                                if (heartbeat != null) await Task.WhenAny(heartbeat, Task.Delay(1000)).ConfigureAwait(false);
                                pipe.Dispose();
                            }
                        }
                    }
                }
            }
        }

        private static async Task<Process> LaunchHelperAsync(ProcessStartInfo start, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var launching = Task.Run(() => Process.Start(start));
            return await AwaitOwnedLaunchAsync(launching, token, 30000).ConfigureAwait(false);
        }

        private static async Task<Process> AwaitOwnedLaunchAsync(Task<Process> launching, CancellationToken token, int timeoutMilliseconds)
        {
            ObserveFailure(launching);
            if (await Task.WhenAny(launching, Task.Delay(timeoutMilliseconds, token)).ConfigureAwait(false) == launching)
                return await launching.ConfigureAwait(false);
            // ShellExecute/UAC cannot safely be cancelled by killing a system UI.
            // Close our pipe now and only dispose a late process handle; it can
            // never receive GO from this abandoned session.
            var cleanup = launching.ContinueWith(completed =>
            {
                using (completed.Result) { }
            }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            ObserveFailure(cleanup);
            token.ThrowIfCancellationRequested();
            throw new TimeoutException("Die Administratorfreigabe wurde nach 30 Sekunden nicht bestätigt. Eine offene UAC-Abfrage bitte ablehnen; kein spätes Anwenden.");
        }

        private static NamedPipeServerStream CreatePipe(string name)
        {
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            using (var identity = WindowsIdentity.GetCurrent())
            {
                if (identity.User == null) throw new InvalidOperationException("Keine Windows-Benutzeridentität für den Gamma-Kanal.");
                security.AddAccessRule(new PipeAccessRule(identity.User, PipeAccessRights.FullControl, AccessControlType.Allow));
            }
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                PipeAccessRights.FullControl, AccessControlType.Allow));
            return new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 4096, 4096, security);
        }

        private static async Task ConnectAsync(NamedPipeServerStream pipe, Process helper, CancellationToken token)
        {
            var connect = pipe.WaitForConnectionAsync();
            ObserveFailure(connect);
            var elapsed = Stopwatch.StartNew();
            while (!connect.IsCompleted)
            {
                token.ThrowIfCancellationRequested();
                if (helper.HasExited) throw new InvalidOperationException("Der Gamma-Helfer endete vor dem Handshake (Exitcode " + helper.ExitCode + ").");
                if (elapsed.ElapsedMilliseconds > 30000) throw new TimeoutException("Kein Gamma-Helfer-Handshake innerhalb von 30 Sekunden.");
                await Task.Delay(50, token).ConfigureAwait(false);
            }
            await connect.ConfigureAwait(false);
        }

        private sealed class HeartbeatResult
        {
            public string Failure;
            public bool AuthorizationLost;
        }

        private async Task<HeartbeatResult> HeartbeatAsync(NamedPipeServerStream pipe, ProgramGammaApproval approval, CancellationToken stop)
        {
            var result = new HeartbeatResult();
            // The full approval check enumerates processes and reads registry and
            // file metadata. Revalidate it at most once per second; every <=5 s
            // native lease still depends on the per-heartbeat UI acknowledgment.
            var sinceValidation = Stopwatch.StartNew();
            var validated = false;
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    if (!await OwnerUiRespondsAsync(stop).ConfigureAwait(false))
                    {
                        if (!stop.IsCancellationRequested)
                        {
                            result.AuthorizationLost = true;
                            result.Failure = "Die Bedienoberfläche antwortet nicht. Erzwingen wurde gestoppt.";
                        }
                        break;
                    }
                    if (!validated || sinceValidation.ElapsedMilliseconds >= ApprovalRevalidationMilliseconds)
                    {
                        if (!approval.IsStillValid())
                        {
                            result.AuthorizationLost = true;
                            result.Failure = "Windows-, DWM- oder Gamma-Freigabe verloren. Erzwingen wurde gestoppt.";
                            break;
                        }
                        validated = true;
                        sinceValidation.Restart();
                    }
                    await SendByteAsync(pipe, 1).ConfigureAwait(false);
                    await Task.Delay(250, stop).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { result.Failure = "Gamma-Verbindung verloren: " + ex.Message; }
            try { if (pipe.IsConnected) await SendByteAsync(pipe, 2).ConfigureAwait(false); }
            catch (Exception ex) { if (result.Failure == null) result.Failure = "Kein bestätigtes Stop-Signal: " + ex.Message; }
            return result;
        }

        private async Task<bool> OwnerUiRespondsAsync(CancellationToken stop)
        {
            if (_ownerDispatcher == null || _ownerDispatcher.HasShutdownStarted || _ownerDispatcher.HasShutdownFinished) return false;
            var acknowledgment = _ownerDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { }));
            if (await Task.WhenAny(acknowledgment.Task, Task.Delay(750, stop)).ConfigureAwait(false) != acknowledgment.Task)
            {
                acknowledgment.Abort();
                return false;
            }
            return acknowledgment.Status == DispatcherOperationStatus.Completed && !stop.IsCancellationRequested;
        }

        private static async Task SendByteAsync(NamedPipeServerStream pipe, byte command)
        {
            var write = pipe.WriteAsync(new[] { command }, 0, 1);
            ObserveFailure(write);
            if (await Task.WhenAny(write, Task.Delay(500)).ConfigureAwait(false) != write)
            {
                pipe.Dispose();
                throw new TimeoutException("Der Gamma-Steuerkanal nimmt keine Befehle an.");
            }
            await write.ConfigureAwait(false);
        }

        private sealed class ReadOutcome
        {
            public ProgramGammaNativeEvent Completion;
            public string Failure;
        }

        private static async Task<ReadOutcome> ReadEventsAsync(Stream pipe, Action<ProgramGammaNativeEvent> onEvent)
        {
            var result = new ReadOutcome();
            var buffer = new byte[1024];
            var line = new StringBuilder();
            while (true)
            {
                var count = await pipe.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                if (count == 0) return result;
                for (var index = 0; index < count; index++)
                {
                    if (buffer[index] == '\n')
                    {
                        if (line.Length == 0) continue;
                        var item = ProgramGammaNativeEvent.Parse(line.ToString());
                        line.Clear();
                        onEvent(item);
                        if (item.Event == "conflicting_hook_module")
                            result.Failure = "Eine andere DWM-Hook-Version ist noch geladen. Erst nach einer neuen DWM-Sitzung erneut global testen und anwenden; es erfolgt kein automatischer Neustart.";
                        if (item.Event == "managed_surface_rejected")
                            result.Failure = "Die SDR-Fläche oder ihr Farbraum hat sich geändert. Erzwingen wurde zurückgenommen; das laufende Fenster erneut testen.";
                        if (item.Event == "complete") { result.Completion = item; return result; }
                    }
                    else if (buffer[index] != '\r')
                    {
                        // The controller's JSON protocol emits escaped ASCII only.
                        if (buffer[index] < 0x20 || buffer[index] > 0x7e || line.Length >= 8192)
                            throw new InvalidDataException("Die Gamma-Helferantwort ist kein begrenztes JSON-Ereignis.");
                        line.Append((char)buffer[index]);
                    }
                }
            }
        }

        private static void ObserveFailure(Task task) => task.ContinueWith(completed =>
        {
            var ignored = completed.Exception;
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
