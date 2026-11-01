using HDRCalibrate.Core;
using HDRCalibrate.Dwm;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml;
using System.Xml.Serialization;

namespace HDRCalibrate.Services
{
    /// <summary>
    /// The UAC prompt was declined before any helper started. DWM, the restart
    /// guard and every proof are unchanged, so callers must not treat it as a
    /// failed DWM operation.
    /// </summary>
    public sealed class DwmElevationCancelledException : OperationCanceledException
    {
        public DwmElevationCancelledException(Exception inner)
            : base("Die Administratorabfrage wurde abgebrochen; DWM wurde nicht verändert.", inner)
        {
        }
    }

    public sealed class DwmPatchService
    {
        private const int HelperTimeoutMilliseconds = 60000;
        private const int TimedTestHelperTimeoutMilliseconds = 30000;
        private const int SupervisedApplyHelperTimeoutMilliseconds = 90000;
        private readonly DwmRestartCooldown _restartCooldown;
        private readonly Func<bool> _isAnyHelperRunning;
        // Set when the app stopped waiting for a started helper (its watchdog may
        // still restart DWM later) or could not record a helper's end. The guard
        // then holds in memory until no helper runs and the end is recorded.
        private int _helperAbandoned;
        private readonly XmlSerializer _requestSerializer = new XmlSerializer(typeof(DwmPatchRequest));
        private readonly XmlSerializer _responseSerializer = new XmlSerializer(typeof(DwmPatchResponse));

        public DwmPatchService() : this(null)
        {
        }

        public DwmPatchService(DwmRestartCooldown restartCooldown, Func<bool> isAnyHelperRunning = null)
        {
            _restartCooldown = restartCooldown ?? new DwmRestartCooldown();
            _isAnyHelperRunning = isAnyHelperRunning ?? IsAnyHelperRunning;
        }

        public TimeSpan GetRestartCooldownRemaining()
        {
            var held = HoldWhileAbandonedHelperRuns();
            var remaining = _restartCooldown.GetRemaining();
            return held > remaining ? held : remaining;
        }

        /// <summary>
        /// True when a started helper (or a watchdog it started) may still restart
        /// DWM: it is still running, it timed out (even if it was killed since), or
        /// its outcome was not finalized.
        /// </summary>
        internal static bool HelperMayStillRun(bool started, bool completed, bool timedOut, bool outcomeFinalized)
        {
            return started && (!completed || timedOut || !outcomeFinalized);
        }

        private void MarkHelperAbandoned()
        {
            Volatile.Write(ref _helperAbandoned, 1);
            TryMarkObservedRestart();
        }

        private void RecordHelperEnd()
        {
            // The request marker may already be older than the guard (UAC wait,
            // helper run time), so an unwritten end must not lift the guard.
            if (!TryMarkObservedRestart()) Volatile.Write(ref _helperAbandoned, 1);
        }

        /// <returns>
        /// The full guard while an abandoned helper may still restart DWM, held in
        /// memory as well: a marker write that fails must never lift the guard.
        /// </returns>
        private TimeSpan HoldWhileAbandonedHelperRuns()
        {
            if (Volatile.Read(ref _helperAbandoned) == 0) return TimeSpan.Zero;
            if (_isAnyHelperRunning())
            {
                TryMarkObservedRestart();
                return DwmRestartCooldown.Duration;
            }
            // Its exit time is unknown (polling stops while the window is hidden),
            // so the guard counts from the moment the exit is noticed, and only
            // once that moment is recorded.
            if (!TryMarkObservedRestart()) return DwmRestartCooldown.Duration;
            Volatile.Write(ref _helperAbandoned, 0);
            return TimeSpan.Zero;
        }

        private void EnforceAbandonedHelperHold()
        {
            var held = HoldWhileAbandonedHelperRuns();
            if (held > TimeSpan.Zero) throw new DwmRestartCooldownException(held);
        }

        private static bool IsAnyHelperRunning()
        {
            var processes = Process.GetProcessesByName("HDRCalibrate.DwmHelper");
            try { return processes.Length != 0; }
            finally { foreach (var process in processes) process.Dispose(); }
        }

        public DwmPatchResponse Preflight(CalibrationSettings settings)
        {
            return new DwmPatchEngine().Preflight(settings);
        }

        public DwmPatchResponse Apply(CalibrationSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            DwmPatchSafety.EnsureLivePatchEnabled();
            return InvokeElevated(DwmPatchCommand.Apply, settings);
        }

        public DwmPatchResponse ApplySupervised(
            CalibrationSettings settings,
            Action<DwmPatchResponse> persistActiveState,
            WindowsEnvironmentIdentity expectedWindowsEnvironment,
            StartupApprovalFileProof startupApproval = null)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (persistActiveState == null) throw new ArgumentNullException(nameof(persistActiveState));
            WindowsEnvironmentGuard.RequireCurrent(expectedWindowsEnvironment);
            startupApproval?.RequireUnchanged();
            DwmPatchSafety.EnsureLivePatchEnabled();
            return InvokeSupervisedApply(settings, persistActiveState, expectedWindowsEnvironment.Clone(), startupApproval);
        }

        public DwmPatchResponse Reset()
        {
            return InvokeElevated(DwmPatchCommand.Reset, new CalibrationSettings());
        }

        public DwmPatchResponse ResetForRecovery()
        {
            return InvokeElevated(DwmPatchCommand.SafetyReset, new CalibrationSettings());
        }

        public DwmPatchResponse RunTimedTest(CalibrationSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            TimedDwmTestRunner.ValidateSettings(settings);
            DwmPatchSafety.EnsureTimedTestEnabled();
            return InvokeElevated(DwmPatchCommand.TimedTest, settings);
        }

        public static int GetCurrentSessionDwmProcessId()
        {
            var sessionId = Process.GetCurrentProcess().SessionId;
            foreach (var process in Process.GetProcessesByName("dwm").OrderBy(item => item.Id))
            {
                using (process)
                {
                    try
                    {
                        if (!process.HasExited && process.SessionId == sessionId) return process.Id;
                    }
                    catch
                    {
                        // A process can exit while the process list is enumerated.
                    }
                }
            }
            return 0;
        }

        public static long TryGetProcessStartTimeUtcTicks(int processId)
        {
            if (processId <= 0) return 0;
            try
            {
                using (var process = Process.GetProcessById(processId))
                    return process.StartTime.ToUniversalTime().Ticks;
            }
            catch
            {
                return 0;
            }
        }

        public static bool IsAppliedStateCurrent(PersistedDwmState state)
        {
            if (state == null || !state.WasApplied || state.DwmProcessId <= 0) return false;
            if (state.SessionId != Process.GetCurrentProcess().SessionId) return false;
            if (GetCurrentSessionDwmProcessId() != state.DwmProcessId) return false;
            // Without a start time a reused PID would look like the patched DWM.
            if (state.DwmStartTimeUtcTicks <= 0) return false;
            var currentStart = TryGetProcessStartTimeUtcTicks(state.DwmProcessId);
            return currentStart != 0 && currentStart == state.DwmStartTimeUtcTicks;
        }

        private DwmPatchResponse InvokeElevated(DwmPatchCommand command, CalibrationSettings settings)
        {
            var helperPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "HDRCalibrate.DwmHelper.exe");
            if (!File.Exists(helperPath))
                throw new FileNotFoundException("Der erhöhte DWM-Helfer fehlt neben HDRCalibrate.exe.", helperPath);

            var requestRoot = DwmIpcRoot.GetDefault();
            Directory.CreateDirectory(requestRoot);
            long? restartMark = null;
            if (command != DwmPatchCommand.SafetyReset && command != DwmPatchCommand.Preflight)
            {
                EnforceAbandonedHelperHold();
                restartMark = _restartCooldown.EnforceAndMark();
            }
            var token = Guid.NewGuid().ToString("N");
            var requestPath = Path.Combine(requestRoot, "request-" + token + ".xml");
            var responsePath = Path.Combine(requestRoot, "response-" + token + ".xml");
            var helperStarted = false;
            var helperCompleted = false;
            var helperTimedOut = false;
            // A test supervisor that failed before a confirmed reset may have left
            // its independent watchdog running; it can still restart DWM.
            var outcomeFinalized = command != DwmPatchCommand.TimedTest;

            try
            {
                using (var stream = new FileStream(requestPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                {
                    _requestSerializer.Serialize(stream, new DwmPatchRequest
                    {
                        Command = command,
                        Settings = settings.Clone()
                    });
                    stream.Flush(true);
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = helperPath,
                    // The root lets an over-the-shoulder elevated helper (another account)
                    // use this user's request directory instead of its own profile.
                    Arguments = "--request " + QuoteArgument(requestPath) + " --response " + QuoteArgument(responsePath)
                        + " --" + DwmIpcRoot.ArgumentName + " " + QuoteArgument(requestRoot),
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                Process helper;
                try
                {
                    helper = Process.Start(startInfo);
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                    TryRevertUnusedRestartMark(restartMark);
                    throw new DwmElevationCancelledException(ex);
                }

                if (helper == null)
                {
                    TryRevertUnusedRestartMark(restartMark);
                    throw new InvalidOperationException("Der DWM-Helfer konnte nicht gestartet werden.");
                }
                using (helper)
                {
                    helperStarted = true;
                    var timeout = command == DwmPatchCommand.TimedTest
                        ? TimedTestHelperTimeoutMilliseconds
                        : HelperTimeoutMilliseconds;
                    helperCompleted = helper.WaitForExit(timeout);
                    if (!helperCompleted)
                    {
                        helperTimedOut = true;
                        if (command == DwmPatchCommand.TimedTest)
                        {
                            try
                            {
                                helper.Kill();
                                helperCompleted = helper.WaitForExit(2000);
                            }
                            catch
                            {
                                // The separately started elevated watchdog remains responsible
                                // for terminating the worker and resetting DWM.
                            }
                        }
                        throw new TimeoutException(
                            command == DwmPatchCommand.TimedTest
                                ? "Der Test-Supervisor antwortete nicht innerhalb von 30 Sekunden. Der unabhängige Watchdog bleibt aktiv und setzt DWM ungepatcht zurück."
                                : "Der DWM-Helfer ist nach 60 Sekunden noch aktiv. Bitte nicht erneut anwenden, bis DWM wieder stabil läuft.");
                    }
                }

                for (var attempt = 0; attempt < 20 && !File.Exists(responsePath); attempt++) Thread.Sleep(25);
                if (!File.Exists(responsePath))
                    throw new InvalidDataException("Der DWM-Helfer hat keine Antwortdatei erzeugt.");

                DwmPatchResponse response;
                using (var stream = new FileStream(responsePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = XmlReader.Create(stream, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null
                }))
                    response = (DwmPatchResponse)_responseSerializer.Deserialize(reader);
                if (response == null) throw new InvalidDataException("Die Antwort des DWM-Helfers ist leer.");
                if (response.Success || response.WatchdogResetUtcTicks > 0) outcomeFinalized = true;
                if (!response.Success)
                {
                    var diagnostic = response.Diagnostics == null
                        ? string.Empty
                        : string.Join(" | ", response.Diagnostics.Where(item => !string.IsNullOrWhiteSpace(item)));
                    throw new InvalidOperationException(
                        response.Message + (string.IsNullOrWhiteSpace(diagnostic) ? string.Empty : " (" + diagnostic + ")"));
                }
                return response;
            }
            finally
            {
                // Killing a timed-out test supervisor ends only that process; its
                // independent watchdog may still restart DWM afterwards.
                if (HelperMayStillRun(helperStarted, helperCompleted, helperTimedOut, outcomeFinalized)) MarkHelperAbandoned();
                else if (helperStarted) RecordHelperEnd();
                // On timeout the elevated helper may still need both files. Leave them in place.
                if (!helperStarted || helperCompleted)
                {
                    TryDelete(requestPath);
                    TryDelete(responsePath);
                    TryDelete(responsePath + ".tmp");
                }
            }
        }

        private DwmPatchResponse InvokeSupervisedApply(
            CalibrationSettings settings,
            Action<DwmPatchResponse> persistActiveState,
            WindowsEnvironmentIdentity expectedWindowsEnvironment,
            StartupApprovalFileProof startupApproval)
        {
            var helperPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "HDRCalibrate.DwmHelper.exe");
            if (!File.Exists(helperPath))
                throw new FileNotFoundException("Der erhöhte DWM-Helfer fehlt neben HDRCalibrate.exe.", helperPath);

            var requestRoot = DwmIpcRoot.GetDefault();
            Directory.CreateDirectory(requestRoot);
            EnforceAbandonedHelperHold();
            var restartMark = _restartCooldown.EnforceAndMark();

            var token = Guid.NewGuid().ToString("N");
            var requestPath = Path.Combine(requestRoot, "request-" + token + ".xml");
            var responsePath = Path.Combine(requestRoot, "response-" + token + ".xml");
            var activationPath = Path.Combine(requestRoot, "apply-activation-" + token + ".xml");
            var commitPath = Path.Combine(requestRoot, "apply-commit-" + token + ".xml");
            var commitAcceptedPath = Path.Combine(requestRoot, "apply-commit-accepted-" + token + ".xml");
            var recoveryPath = Path.Combine(requestRoot, "apply-recovery-" + token + ".xml");
            var helperStarted = false;
            var helperCompleted = false;
            var commitAttempted = false;
            var commitWritten = false;
            var outcomeFinalized = false;
            DwmPatchResponse savedActivation = null;
            Exception commitError = null;

            try
            {
                WriteXmlNew(requestPath, _requestSerializer, new DwmPatchRequest
                {
                    Command = DwmPatchCommand.SupervisedApply,
                    Settings = settings.Clone(),
                    ExpectedWindowsEnvironment = expectedWindowsEnvironment,
                    StartupApproval = startupApproval,
                    CorrelationToken = token,
                    ActivationPath = activationPath,
                    CommitPath = commitPath,
                    CommitAcceptedPath = commitAcceptedPath,
                    RecoveryResponsePath = recoveryPath
                });

                var startInfo = new ProcessStartInfo
                {
                    FileName = helperPath,
                    // The root lets an over-the-shoulder elevated helper (another account)
                    // use this user's request directory instead of its own profile.
                    Arguments = "--request " + QuoteArgument(requestPath) + " --response " + QuoteArgument(responsePath)
                        + " --" + DwmIpcRoot.ArgumentName + " " + QuoteArgument(requestRoot),
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                Process helper;
                try
                {
                    helper = Process.Start(startInfo);
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                    TryRevertUnusedRestartMark(restartMark);
                    throw new DwmElevationCancelledException(ex);
                }

                if (helper == null)
                {
                    TryRevertUnusedRestartMark(restartMark);
                    throw new InvalidOperationException("Der DWM-Helfer konnte nicht gestartet werden.");
                }
                using (helper)
                {
                    helperStarted = true;
                    var stopwatch = Stopwatch.StartNew();
                    while (!helper.HasExited
                        && stopwatch.ElapsedMilliseconds < SupervisedApplyHelperTimeoutMilliseconds)
                    {
                        if (!commitAttempted
                            && TryReadResponseFile(activationPath, out var activation))
                        {
                            commitAttempted = true;
                            try
                            {
                                SupervisedApplyProtocol.ValidateResponse(activation, token, false);
                                WindowsEnvironmentGuard.RequireCurrent(expectedWindowsEnvironment);
                                startupApproval?.RequireUnchanged();
                                persistActiveState(activation);
                                savedActivation = activation;
                                var commit = SupervisedApplyProtocol.CreateCommit(activation, token);
                                WriteXmlNew(commitPath, _responseSerializer, commit);
                                commitWritten = true;
                            }
                            catch (Exception ex)
                            {
                                commitError = ex;
                            }
                        }
                        helper.WaitForExit(25);
                    }

                    helperCompleted = helper.HasExited;
                    if (!helperCompleted)
                        throw new TimeoutException(
                            "Der überwachte DWM-Helfer antwortete nicht innerhalb von 90 Sekunden. " +
                            "Sein unabhängiger Watchdog bleibt für die ungepatchte Rücksetzung verantwortlich.");
                    helper.WaitForExit();
                }

                var response = ReadResponseFile(responsePath);
                if (!response.Success && response.RecoveryPerformed && response.WatchdogArmed
                    && response.DwmProcessId > 0 && response.DwmStartTimeUtcTicks > 0
                    && response.WatchdogResetUtcTicks > 0
                    && string.Equals(response.CorrelationToken, token, StringComparison.Ordinal))
                    outcomeFinalized = true;
                if (commitError != null)
                {
                    throw new AggregateException(
                        "Der aktive DWM-Zustand konnte nicht sicher gespeichert werden; der Watchdog hat deshalb keinen Commit erhalten.",
                        commitError,
                        new InvalidOperationException(response.Message));
                }
                if (!response.Success)
                {
                    var diagnostic = response.Diagnostics == null
                        ? string.Empty
                        : string.Join(" | ", response.Diagnostics.Where(item => !string.IsNullOrWhiteSpace(item)));
                    throw new InvalidOperationException(
                        response.Message + (string.IsNullOrWhiteSpace(diagnostic) ? string.Empty : " (" + diagnostic + ")"));
                }
                if (!commitWritten)
                    throw new InvalidDataException("Der überwachte Apply-Pfad endete ohne geschriebenen Zustands-Commit.");
                SupervisedApplyProtocol.ValidateResponse(response, token, true);
                SupervisedApplyProtocol.ValidateCommittedApply(token, savedActivation, response);
                outcomeFinalized = true;
                return response;
            }
            finally
            {
                // A supervisor that exited may still have left recovery to its watchdog.
                if (HelperMayStillRun(helperStarted, helperCompleted, false, outcomeFinalized)) MarkHelperAbandoned();
                else if (helperStarted) RecordHelperEnd();
                // The supervisor can exit after handing recovery to an independent
                // watchdog. Its exit alone does not make the shared IPC files disposable.
                if (!helperStarted || outcomeFinalized)
                {
                    foreach (var path in new[]
                    {
                        requestPath, responsePath, activationPath, commitPath,
                        commitAcceptedPath, recoveryPath
                    })
                    {
                        TryDelete(path);
                        TryDelete(path + ".tmp");
                    }
                }
            }
        }

        private DwmPatchResponse ReadResponseFile(string path)
        {
            if (!File.Exists(path))
                throw new InvalidDataException("Der DWM-Helfer hat keine Antwortdatei erzeugt.");
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            }))
                return (DwmPatchResponse)_responseSerializer.Deserialize(reader)
                    ?? throw new InvalidDataException("Die Antwort des DWM-Helfers ist leer.");
        }

        private bool TryReadResponseFile(string path, out DwmPatchResponse response)
        {
            response = null;
            if (!File.Exists(path)) return false;
            try
            {
                response = ReadResponseFile(path);
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

        private static void WriteXmlNew(string path, XmlSerializer serializer, object value)
        {
            var temporary = path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                serializer.Serialize(stream, value);
                stream.Flush(true);
            }
            File.Move(temporary, path);
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private bool TryMarkObservedRestart()
        {
            // A started helper can restart DWM more than once (a timed test patches
            // and then resets). Count the guard from its end. If this write fails,
            // the earlier request marker still guards (fail closed).
            try
            {
                _restartCooldown.MarkRestart();
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
            {
                Trace.TraceWarning("DWM-Neustartsperre nach Helferende nicht aktualisiert: " + ex.Message);
                return false;
            }
        }

        private void TryRevertUnusedRestartMark(long? restartMark)
        {
            // No helper started, so DWM was not restarted. If the marker cannot be
            // released, the guard simply stays in place (fail closed).
            if (!restartMark.HasValue) return;
            try { _restartCooldown.RevertUnusedMark(restartMark.Value); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Trace.TraceWarning("DWM-Neustartsperre nach abgebrochenem Start nicht freigegeben: " + ex.Message);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // Stale IPC files contain settings only and are harmless.
            }
        }
    }
}
