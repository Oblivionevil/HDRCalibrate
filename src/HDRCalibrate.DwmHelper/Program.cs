using HDRCalibrate.Dwm;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml;
using System.Xml.Serialization;

namespace HDRCalibrate.DwmHelper
{
    internal static class Program
    {
        private const string OperationMutexName = "Local\\HDRCalibrate.DwmOperation";
        private const string TimedTestMutexName = "Local\\HDRCalibrate.DwmTimedTestSupervisor";
        private const int ChildReadyTimeoutMilliseconds = 2000;
        private const int RecoveryTimeoutMilliseconds = 20000;

        private static readonly XmlSerializer RequestSerializer = new XmlSerializer(typeof(DwmPatchRequest));
        private static readonly XmlSerializer ResponseSerializer = new XmlSerializer(typeof(DwmPatchResponse));
        private static readonly XmlSerializer IdentitySerializer = new XmlSerializer(typeof(TimedTestProcessIdentity));

        private static int Main(string[] args)
        {
            string responsePath = null;
            DwmPatchResponse response;
            try
            {
                var arguments = ParseArguments(args);
                if (!arguments.TryGetValue("request", out var requestArgument)
                    || !arguments.TryGetValue("response", out var responseArgument))
                    throw new ArgumentException("Erwartet werden --request und --response.");

                var requestPath = ValidateIpcPath(requestArgument);
                responsePath = ValidateIpcPath(responseArgument);
                var request = ReadRequest(requestPath);
                if (request.Command == DwmPatchCommand.Unknown)
                    throw new InvalidDataException("Der DWM-Befehl fehlt oder ist ungültig.");

                switch (request.Command)
                {
                    case DwmPatchCommand.Preflight:
                        response = new DwmPatchEngine().Preflight(request.Settings);
                        break;
                    case DwmPatchCommand.Apply:
                        DwmPatchSafety.EnsureLivePatchEnabled();
                        EnsureNoTimedTestActive();
                        response = RunWithOperationMutex(() => new DwmPatchEngine().Apply(request.Settings), 0);
                        break;
                    case DwmPatchCommand.Reset:
                        EnsureNoTimedTestActive();
                        response = RunWithOperationMutex(() => new DwmPatchEngine().Reset(), 3000);
                        break;
                    case DwmPatchCommand.SafetyReset:
                        response = RunWithOperationMutex(() => new DwmPatchEngine().Reset(), 3000);
                        break;
                    case DwmPatchCommand.TimedTest:
                        DwmPatchSafety.EnsureTimedTestEnabled();
                        response = RunSupervisedTimedTest(request);
                        break;
                    case DwmPatchCommand.TimedTestWorker:
                        DwmPatchSafety.EnsureTimedTestEnabled();
                        ValidateInternalRequest(request);
                        response = RunWithOperationMutex(
                            () => RunTimedTestWorker(request, responsePath),
                            ChildReadyTimeoutMilliseconds);
                        break;
                    case DwmPatchCommand.TimedTestWatchdog:
                        DwmPatchSafety.EnsureTimedTestEnabled();
                        ValidateInternalRequest(request);
                        response = RunTimedTestWatchdog(request);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(request.Command));
                }
            }
            catch (Exception ex)
            {
                response = ErrorResponse(ex);
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(responsePath)) WriteResponse(responsePath, response);
            }
            catch
            {
                return 2;
            }
            return response.Success ? 0 : 1;
        }

        private static DwmPatchResponse RunSupervisedTimedTest(DwmPatchRequest outerRequest)
        {
            TimedDwmTestRunner.ValidateSettings(outerRequest.Settings);
            using (var testMutex = new Mutex(false, TimedTestMutexName))
            {
                var ownsTestMutex = TryAcquire(testMutex, 0);
                if (!ownsTestMutex)
                    throw new InvalidOperationException("Ein anderer überwachter 5-Sekunden-Test läuft bereits.");
                try
                {
                    return RunSupervisedTimedTestCore(outerRequest.Settings);
                }
                finally
                {
                    testMutex.ReleaseMutex();
                }
            }
        }

        private static DwmPatchResponse RunSupervisedTimedTestCore(HDRCalibrate.Core.CalibrationSettings settings)
        {
            var token = Guid.NewGuid().ToString("N");
            var paths = CreateTimedTestPaths(token);
            Process watchdog = null;
            Process worker = null;
            TimedTestProcessIdentity watchdogIdentity = null;
            TimedTestProcessIdentity workerIdentity = null;
            var helperPath = GetCurrentExecutablePath();
            var supervisorClock = new Stopwatch();
            DwmPatchResponse applied = null;
            DwmPatchResponse reset = null;
            Exception testFailure = null;

            try
            {
                WriteRequest(paths.WatchdogRequest, new DwmPatchRequest
                {
                    Command = DwmPatchCommand.TimedTestWatchdog,
                    CorrelationToken = token,
                    ReadyPath = paths.WatchdogReady,
                    StartGatePath = paths.StartGate,
                    WorkerIdentityPath = paths.WorkerIdentity,
                    WorkerResponsePath = paths.WorkerResponse,
                    ActivationPath = paths.Activation,
                    RecoveryResponsePath = paths.RecoveryResponse
                });
                WriteRequest(paths.WorkerRequest, new DwmPatchRequest
                {
                    Command = DwmPatchCommand.TimedTestWorker,
                    Settings = settings.Clone(),
                    CorrelationToken = token,
                    StartGatePath = paths.StartGate,
                    WorkerIdentityPath = paths.WorkerIdentity,
                    WorkerResponsePath = paths.WorkerResponse,
                    ActivationPath = paths.Activation,
                    RecoveryResponsePath = paths.RecoveryResponse
                });

                watchdog = StartChild(helperPath, paths.WatchdogRequest, paths.WatchdogStatus);
                watchdogIdentity = GetProcessIdentity(watchdog);
                WaitForSuccessfulResponse(paths.WatchdogReady, ChildReadyTimeoutMilliseconds,
                    "Der unabhängige DWM-Watchdog wurde nicht rechtzeitig bereit.");

                worker = StartChild(helperPath, paths.WorkerRequest, paths.WorkerResponse);
                workerIdentity = GetProcessIdentity(worker);
                WriteIdentity(paths.WorkerIdentity, workerIdentity);
                WriteResponse(paths.StartGate, new DwmPatchResponse
                {
                    Success = true,
                    WatchdogArmed = true,
                    Message = "Startsignal für den überwachten Test."
                });
                supervisorClock.Start();

                var fallbackDeadline = TimedDwmTestRunner.SetupDeadlineMilliseconds
                    + TimedDwmTestRunner.SupervisorGraceMilliseconds;
                while (supervisorClock.ElapsedMilliseconds < RecoveryTimeoutMilliseconds)
                {
                    if (TryReadResponse(paths.Activation, out var activation) && activation.Success)
                    {
                        applied = activation;
                        fallbackDeadline = checked((int)supervisorClock.ElapsedMilliseconds
                            + TimedDwmTestRunner.GetRemainingDurationMilliseconds(
                                activation.PatchActivatedUtcTicks, DateTime.UtcNow)
                            + TimedDwmTestRunner.SupervisorGraceMilliseconds);
                    }
                    if (TryReadResponse(paths.WorkerResponse, out var workerResponse))
                    {
                        if (workerResponse.Success) applied = workerResponse;
                        else testFailure = new InvalidOperationException(workerResponse.Message);
                    }
                    if (TryReadResponse(paths.RecoveryResponse, out var recoveryResponse) && recoveryResponse.Success)
                    {
                        reset = recoveryResponse;
                        break;
                    }

                    if (watchdog.HasExited || supervisorClock.ElapsedMilliseconds >= fallbackDeadline)
                    {
                        reset = RecoverOrTakeOver(
                            token, paths, workerIdentity, watchdog, watchdogIdentity,
                            watchdog.HasExited ? "Watchdog-Prozess endete vor der bestätigten Rücksetzung."
                                : "Supervisor-Sicherheitsfrist erreicht.");
                        break;
                    }
                    Thread.Sleep(10);
                }

                if (reset == null)
                    reset = RecoverOrTakeOver(
                        token, paths, workerIdentity, watchdog, watchdogIdentity,
                        "Die überwachte Rücksetzung überschritt die absolute Sicherheitsfrist.");

                if (applied == null && TryReadResponse(paths.Activation, out var finalActivation))
                    applied = finalActivation;
                if (applied == null && TryReadResponse(paths.WorkerResponse, out var finalWorker))
                    applied = finalWorker;
                if (applied == null || !applied.Success)
                    testFailure = testFailure ?? new InvalidOperationException(
                        applied == null ? "Der Patch-Worker bestätigte keine Aktivierung."
                            : applied.Message);

                TryTerminateExact(workerIdentity);
                if (watchdog != null && !watchdog.HasExited) watchdog.WaitForExit(2000);
                if (watchdog != null && !watchdog.HasExited) TryTerminateExact(watchdogIdentity);

                if (testFailure != null)
                {
                    return new DwmPatchResponse
                    {
                        Success = false,
                        WatchdogArmed = true,
                        WatchdogResetUtcTicks = reset.WatchdogResetUtcTicks,
                        DwmProcessId = reset.DwmProcessId,
                        DwmStartTimeUtcTicks = reset.DwmStartTimeUtcTicks,
                        DwmCoreVersion = reset.DwmCoreVersion,
                        Message = "Der Test wurde sicher zurückgesetzt, die Aktivierung schlug jedoch fehl: " + testFailure.Message,
                        Diagnostics = reset.Diagnostics ?? new List<string>()
                    };
                }

                var measured = MeasureActiveDuration(applied.PatchActivatedUtcTicks, reset.WatchdogResetUtcTicks);
                return TimedDwmTestRunner.Combine(applied, reset, measured);
            }
            finally
            {
                TryTerminateExact(workerIdentity);
                TryTerminateExact(watchdogIdentity);
                if (worker != null) worker.Dispose();
                if (watchdog != null) watchdog.Dispose();
                foreach (var path in paths.All) TryDelete(path);
            }
        }

        private static DwmPatchResponse RunTimedTestWorker(DwmPatchRequest request, string responsePath)
        {
            TimedDwmTestRunner.ValidateSettings(request.Settings);
            WaitForSuccessfulResponse(request.StartGatePath, ChildReadyTimeoutMilliseconds,
                "Das Startsignal des 5-Sekunden-Tests fehlt.");

            var response = new DwmPatchEngine().ApplySupervisedTest(request.Settings, activation =>
            {
                activation.WatchdogArmed = true;
                WriteResponse(request.ActivationPath, activation);
            });
            response.WatchdogArmed = true;
            WriteResponse(responsePath, response);

            // The watchdog owns this process lifetime. Keeping the operation mutex until it
            // terminates us prevents any second mutating helper from racing the timed test.
            Thread.Sleep(Timeout.Infinite);
            throw new InvalidOperationException("Der Patch-Worker wurde unerwartet fortgesetzt.");
        }

        private static DwmPatchResponse RunTimedTestWatchdog(DwmPatchRequest request)
        {
            WriteResponse(request.ReadyPath, new DwmPatchResponse
            {
                Success = true,
                WatchdogArmed = true,
                Message = "Unabhängiger DWM-Watchdog ist bereit."
            });
            WaitForSuccessfulResponse(request.StartGatePath, ChildReadyTimeoutMilliseconds * 2,
                "Der Watchdog erhielt kein Startsignal.");

            var clock = Stopwatch.StartNew();
            var deadline = TimedDwmTestRunner.SetupDeadlineMilliseconds;
            while (clock.ElapsedMilliseconds < deadline)
            {
                if (TryReadResponse(request.ActivationPath, out var activation) && activation.Success)
                {
                    deadline = checked((int)clock.ElapsedMilliseconds
                        + TimedDwmTestRunner.GetRemainingDurationMilliseconds(
                            activation.PatchActivatedUtcTicks, DateTime.UtcNow));
                }
                if (TryReadResponse(request.WorkerResponsePath, out var worker) && !worker.Success)
                    break;
                if (File.Exists(request.WorkerIdentityPath)
                    && TryReadIdentity(request.WorkerIdentityPath, out var identity)
                    && !IsExactProcessAlive(identity))
                    break;
                Thread.Sleep(5);
            }

            var reset = Recover(
                request.CorrelationToken,
                request.WorkerIdentityPath,
                request.RecoveryResponsePath,
                0,
                "Unabhängiger Watchdog");
            return reset;
        }

        private static DwmPatchResponse RecoverOrTakeOver(
            string token,
            TimedTestPaths paths,
            TimedTestProcessIdentity workerIdentity,
            Process watchdog,
            TimedTestProcessIdentity watchdogIdentity,
            string reason)
        {
            var deadline = Stopwatch.StartNew();
            while (deadline.ElapsedMilliseconds < 1000)
            {
                if (TryReadResponse(paths.RecoveryResponse, out var existing) && existing.Success)
                    return existing;
                Thread.Sleep(10);
            }

            try
            {
                return Recover(token, paths.WorkerIdentity, paths.RecoveryResponse, 250, reason);
            }
            catch (TimeoutException)
            {
                while (deadline.ElapsedMilliseconds < 17000)
                {
                    if (TryReadResponse(paths.RecoveryResponse, out var existing) && existing.Success)
                        return existing;
                    if (watchdog != null && watchdog.HasExited) break;
                    Thread.Sleep(20);
                }

                TryTerminateExact(watchdogIdentity);
                TryTerminateExact(workerIdentity);
                return Recover(token, paths.WorkerIdentity, paths.RecoveryResponse, 2000,
                    reason + " Der nicht antwortende Watchdog wurde übernommen.");
            }
        }

        private static DwmPatchResponse Recover(
            string token,
            string workerIdentityPath,
            string recoveryResponsePath,
            int leaderTimeoutMilliseconds,
            string actor)
        {
            using (var leader = new Mutex(false, "Local\\HDRCalibrate.DwmRecoveryLeader-" + token))
            {
                var ownsLeader = TryAcquire(leader, leaderTimeoutMilliseconds);
                if (!ownsLeader)
                    throw new TimeoutException("Ein anderer Recovery-Wächter führt die Rücksetzung bereits aus.");
                try
                {
                    if (TryReadResponse(recoveryResponsePath, out var existing) && existing.Success)
                        return existing;

                    if (TryReadIdentity(workerIdentityPath, out var identity)) TryTerminateExact(identity);
                    if (TryReadIdentity(workerIdentityPath, out identity) && IsExactProcessAlive(identity))
                        throw new InvalidOperationException("Der Patch-Worker konnte vor dem DWM-Reset nicht beendet werden.");
                    var resetStarted = DateTime.UtcNow.Ticks;
                    var reset = RunWithOperationMutex(() => new DwmPatchEngine().Reset(), 3000);
                    reset.WatchdogArmed = true;
                    reset.WatchdogResetUtcTicks = resetStarted;
                    if (reset.Diagnostics == null) reset.Diagnostics = new List<string>();
                    reset.Diagnostics.Insert(0, actor + ": Patch-Worker beendet; ungepatchter DWM-Neustart eingeleitet.");
                    WriteResponse(recoveryResponsePath, reset);
                    return reset;
                }
                finally
                {
                    leader.ReleaseMutex();
                }
            }
        }

        private static DwmPatchResponse RunWithOperationMutex(Func<DwmPatchResponse> action, int timeoutMilliseconds)
        {
            using (var operationMutex = new Mutex(false, OperationMutexName))
            {
                var ownsMutex = TryAcquire(operationMutex, timeoutMilliseconds);
                if (!ownsMutex)
                    throw new InvalidOperationException("Eine andere HDR-Calibrate-DWM-Operation läuft bereits.");
                try
                {
                    return action();
                }
                finally
                {
                    operationMutex.ReleaseMutex();
                }
            }
        }

        private static bool TryAcquire(Mutex mutex, int timeoutMilliseconds)
        {
            try
            {
                return mutex.WaitOne(Math.Max(0, timeoutMilliseconds));
            }
            catch (AbandonedMutexException)
            {
                return true;
            }
        }

        private static void EnsureNoTimedTestActive()
        {
            using (var mutex = new Mutex(false, TimedTestMutexName))
            {
                if (!TryAcquire(mutex, 0))
                    throw new InvalidOperationException("Während des überwachten 5-Sekunden-Tests ist keine zweite DWM-Operation zulässig.");
                mutex.ReleaseMutex();
            }
        }

        private static void ValidateInternalRequest(DwmPatchRequest request)
        {
            if (!TimedDwmTestRunner.IsValidCorrelationToken(request.CorrelationToken))
                throw new InvalidDataException("Der interne Test-Token ist ungültig.");
            var paths = new[]
            {
                request.StartGatePath,
                request.WorkerIdentityPath,
                request.WorkerResponsePath,
                request.ActivationPath,
                request.RecoveryResponsePath
            };
            if (request.Command == DwmPatchCommand.TimedTestWatchdog)
                paths = paths.Concat(new[] { request.ReadyPath }).ToArray();
            foreach (var path in paths)
            {
                var validated = ValidateIpcPath(path);
                if (Path.GetFileName(validated).IndexOf(request.CorrelationToken, StringComparison.OrdinalIgnoreCase) < 0)
                    throw new UnauthorizedAccessException("Eine interne IPC-Datei gehört nicht zum Test-Token.");
            }
        }

        private static Process StartChild(string helperPath, string requestPath, string responsePath)
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = helperPath,
                Arguments = "--request " + QuoteArgument(requestPath) + " --response " + QuoteArgument(responsePath),
                WorkingDirectory = Path.GetDirectoryName(helperPath),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            return process ?? throw new InvalidOperationException("Ein interner DWM-Sicherheitsprozess konnte nicht gestartet werden.");
        }

        private static TimedTestProcessIdentity GetProcessIdentity(Process process)
        {
            for (var attempt = 0; attempt < 50; attempt++)
            {
                try
                {
                    return new TimedTestProcessIdentity
                    {
                        ProcessId = process.Id,
                        StartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks
                    };
                }
                catch when (attempt < 49)
                {
                    Thread.Sleep(10);
                }
            }
            throw new InvalidOperationException("Die Identität eines DWM-Sicherheitsprozesses konnte nicht gelesen werden.");
        }

        private static bool IsExactProcessAlive(TimedTestProcessIdentity identity)
        {
            if (identity == null || identity.ProcessId <= 0 || identity.StartTimeUtcTicks <= 0) return false;
            try
            {
                using (var process = Process.GetProcessById(identity.ProcessId))
                    return !process.HasExited
                        && process.StartTime.ToUniversalTime().Ticks == identity.StartTimeUtcTicks;
            }
            catch
            {
                return false;
            }
        }

        private static void TryTerminateExact(TimedTestProcessIdentity identity)
        {
            if (!IsExactProcessAlive(identity)) return;
            try
            {
                using (var process = Process.GetProcessById(identity.ProcessId))
                {
                    if (process.StartTime.ToUniversalTime().Ticks != identity.StartTimeUtcTicks) return;
                    process.Kill();
                    process.WaitForExit(2000);
                }
            }
            catch
            {
                // Recovery will still restart DWM; a stale/reused PID is never terminated.
            }
        }

        private static int MeasureActiveDuration(long activatedUtcTicks, long resetUtcTicks)
        {
            if (activatedUtcTicks <= 0 || resetUtcTicks <= activatedUtcTicks)
                return TimedDwmTestRunner.DurationMilliseconds;
            return checked((int)Math.Max(0, Math.Min(
                TimedDwmTestRunner.DurationMilliseconds + 1000L,
                TimeSpan.FromTicks(resetUtcTicks - activatedUtcTicks).TotalMilliseconds)));
        }

        private static void WaitForSuccessfulResponse(string path, int timeoutMilliseconds, string error)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < timeoutMilliseconds)
            {
                if (TryReadResponse(path, out var response))
                {
                    if (!response.Success) throw new InvalidOperationException(error + " " + response.Message);
                    return;
                }
                Thread.Sleep(10);
            }
            throw new TimeoutException(error);
        }

        private static Dictionary<string, string> ParseArguments(string[] args)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < args.Length; index++)
            {
                var item = args[index];
                if (!item.StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
                    throw new ArgumentException("Ungültige Befehlszeile.");
                var key = item.Substring(2);
                if (result.ContainsKey(key)) throw new ArgumentException("Doppeltes Befehlszeilenargument: " + key + ".");
                result.Add(key, args[++index]);
            }
            return result;
        }

        private static string ValidateIpcPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentNullException(nameof(path));
            var root = GetRequestRoot();
            Directory.CreateDirectory(root);
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Der HDRCalibrate-Requests-Ordner darf kein Reparse Point sein.");
            var candidate = Path.GetFullPath(path);
            if (!string.Equals(Path.GetDirectoryName(candidate), root, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetExtension(candidate), ".xml", StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("IPC-Dateien müssen im HDRCalibrate-Requests-Ordner liegen.");
            if (File.Exists(candidate) && (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("IPC-Dateien dürfen keine Reparse Points sein.");
            return candidate;
        }

        private static string GetRequestRoot()
        {
            return Path.GetFullPath(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "HDRCalibrate",
                    "Requests"))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static DwmPatchRequest ReadRequest(string path)
        {
            return ReadXml<DwmPatchRequest>(path, RequestSerializer, "Die DWM-Anforderung ist leer.");
        }

        private static void WriteRequest(string path, DwmPatchRequest request)
        {
            WriteXml(path, request, RequestSerializer);
        }

        private static bool TryReadResponse(string path, out DwmPatchResponse response)
        {
            response = null;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
            try
            {
                response = ReadXml<DwmPatchResponse>(ValidateIpcPath(path), ResponseSerializer, "Die DWM-Antwort ist leer.");
                return response != null;
            }
            catch (IOException)
            {
                return false;
            }
            catch (XmlException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static void WriteResponse(string path, DwmPatchResponse response)
        {
            WriteXml(path, response, ResponseSerializer);
        }

        private static bool TryReadIdentity(string path, out TimedTestProcessIdentity identity)
        {
            identity = null;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
            try
            {
                identity = ReadXml<TimedTestProcessIdentity>(ValidateIpcPath(path), IdentitySerializer,
                    "Die Prozessidentität ist leer.");
                return identity != null && identity.ProcessId > 0 && identity.StartTimeUtcTicks > 0;
            }
            catch
            {
                return false;
            }
        }

        private static void WriteIdentity(string path, TimedTestProcessIdentity identity)
        {
            WriteXml(path, identity, IdentitySerializer);
        }

        private static T ReadXml<T>(string path, XmlSerializer serializer, string emptyMessage)
            where T : class
        {
            var validated = ValidateIpcPath(path);
            var info = new FileInfo(validated);
            if (info.Length <= 0 || info.Length > 64 * 1024)
                throw new InvalidDataException("Eine DWM-IPC-Datei besitzt eine ungültige Größe.");
            using (var stream = new FileStream(validated, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            }))
                return (T)serializer.Deserialize(reader) ?? throw new InvalidDataException(emptyMessage);
        }

        private static void WriteXml(string path, object value, XmlSerializer serializer)
        {
            path = ValidateIpcPath(path);
            var temporary = path + ".tmp";
            if (File.Exists(path) || File.Exists(temporary))
                throw new IOException("Die DWM-IPC-Zieldatei existiert bereits.");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                serializer.Serialize(stream, value);
                stream.Flush(true);
            }
            File.Move(temporary, path);
        }

        private static string GetCurrentExecutablePath()
        {
            using (var process = Process.GetCurrentProcess())
            {
                var path = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    throw new FileNotFoundException("Der Pfad des DWM-Helfers konnte nicht bestimmt werden.");
                return Path.GetFullPath(path);
            }
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static DwmPatchResponse ErrorResponse(Exception ex)
        {
            return new DwmPatchResponse
            {
                Success = false,
                LivePatchBlocked = ex is NotSupportedException
                    && string.Equals(ex.Message, DwmPatchSafety.BlockReason, StringComparison.Ordinal),
                Message = ex.Message,
                Diagnostics = new List<string> { ex.GetType().Name + ": " + ex.Message }
            };
        }

        private static TimedTestPaths CreateTimedTestPaths(string token)
        {
            var root = GetRequestRoot();
            Directory.CreateDirectory(root);
            return new TimedTestPaths(root, token);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path);
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
            }
            catch
            {
                // A crashed supervisor may leave only small settings/diagnostic IPC files.
            }
        }

        private sealed class TimedTestPaths
        {
            public TimedTestPaths(string root, string token)
            {
                WatchdogRequest = Make(root, "watchdog-request", token);
                WatchdogStatus = Make(root, "watchdog-status", token);
                WatchdogReady = Make(root, "watchdog-ready", token);
                WorkerRequest = Make(root, "worker-request", token);
                WorkerResponse = Make(root, "worker-response", token);
                WorkerIdentity = Make(root, "worker-identity", token);
                StartGate = Make(root, "start-gate", token);
                Activation = Make(root, "activation", token);
                RecoveryResponse = Make(root, "recovery", token);
                All = new[]
                {
                    WatchdogRequest, WatchdogStatus, WatchdogReady, WorkerRequest,
                    WorkerResponse, WorkerIdentity, StartGate, Activation, RecoveryResponse
                };
            }

            public string WatchdogRequest { get; }
            public string WatchdogStatus { get; }
            public string WatchdogReady { get; }
            public string WorkerRequest { get; }
            public string WorkerResponse { get; }
            public string WorkerIdentity { get; }
            public string StartGate { get; }
            public string Activation { get; }
            public string RecoveryResponse { get; }
            public IEnumerable<string> All { get; }

            private static string Make(string root, string prefix, string token)
            {
                return Path.Combine(root, prefix + "-" + token + ".xml");
            }
        }
    }
}
