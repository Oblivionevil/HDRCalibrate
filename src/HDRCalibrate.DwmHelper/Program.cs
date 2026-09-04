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
        private const string SupervisedMutationMutexName = "Local\\HDRCalibrate.DwmSupervisedMutation";
        private const int ChildReadyTimeoutMilliseconds = 2000;
        private const int RecoveryTimeoutMilliseconds = 20000;
        private const int PersistentApplySetupDeadlineMilliseconds = 30000;
        private const int PersistentApplyCommitDeadlineMilliseconds = 15000;
        private const int PersistentApplyAbsoluteDeadlineMilliseconds = 65000;

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
                        EnsureNoSupervisedMutationActive();
                        response = RunWithOperationMutex(() => new DwmPatchEngine().Apply(request.Settings), 0);
                        break;
                    case DwmPatchCommand.Reset:
                        EnsureNoSupervisedMutationActive();
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
                    case DwmPatchCommand.SupervisedApply:
                        DwmPatchSafety.EnsureLivePatchEnabled();
                        response = RunSupervisedPersistentApply(request);
                        break;
                    case DwmPatchCommand.SupervisedApplyWorker:
                        DwmPatchSafety.EnsureLivePatchEnabled();
                        ValidateInternalRequest(request);
                        response = RunWithOperationMutex(
                            () => RunSupervisedPersistentWorker(request, responsePath),
                            ChildReadyTimeoutMilliseconds);
                        break;
                    case DwmPatchCommand.SupervisedApplyWatchdog:
                        DwmPatchSafety.EnsureLivePatchEnabled();
                        ValidateInternalRequest(request);
                        response = RunSupervisedPersistentWatchdog(request);
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
            using (var testMutex = new Mutex(false, SupervisedMutationMutexName))
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
            long activationObservedAtMilliseconds = -1;

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
                        if (applied == null)
                        {
                            activationObservedAtMilliseconds = supervisorClock.ElapsedMilliseconds;
                            fallbackDeadline = checked((int)activationObservedAtMilliseconds
                                + TimedDwmTestRunner.DurationMilliseconds
                                + TimedDwmTestRunner.SupervisorGraceMilliseconds);
                        }
                        applied = activation;
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

                var measured = MeasureActiveDuration(applied?.PatchActivatedUtcTicks ?? 0, reset.WatchdogResetUtcTicks);
                if (applied != null && applied.Success)
                {
                    if (activationObservedAtMilliseconds < 0)
                    {
                        testFailure = testFailure ?? new InvalidOperationException(
                            "Der Supervisor konnte die Testdauer nicht monoton überwachen.");
                    }
                    else
                    {
                        var observedDuration = supervisorClock.ElapsedMilliseconds - activationObservedAtMilliseconds;
                        var verifiedDuration = reset.TestDurationMilliseconds > 0
                            ? reset.TestDurationMilliseconds
                            : observedDuration;
                        if (verifiedDuration < TimedDwmTestRunner.DurationMilliseconds)
                            testFailure = testFailure ?? new InvalidOperationException(
                                "Der unabhängige Reset erfolgte vor Ablauf der vollständigen Testdauer.");
                        measured = checked((int)Math.Max(
                            TimedDwmTestRunner.DurationMilliseconds,
                            Math.Min(TimedDwmTestRunner.DurationMilliseconds + 1000L, verifiedDuration)));
                    }
                }

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
            var activationObserved = false;
            long activationObservedAtMilliseconds = -1;
            while (clock.ElapsedMilliseconds < deadline)
            {
                if (TryReadResponse(request.ActivationPath, out var activation) && activation.Success)
                {
                    if (!activationObserved)
                    {
                        activationObserved = true;
                        activationObservedAtMilliseconds = clock.ElapsedMilliseconds;
                        deadline = checked((int)clock.ElapsedMilliseconds
                            + TimedDwmTestRunner.DurationMilliseconds);
                    }
                }
                if (TryReadResponse(request.WorkerResponsePath, out var worker) && !worker.Success)
                    break;
                if (File.Exists(request.WorkerIdentityPath)
                    && TryReadIdentity(request.WorkerIdentityPath, out var identity)
                    && !IsExactProcessAlive(identity))
                    break;
                Thread.Sleep(5);
            }

            var verifiedDurationMilliseconds = activationObservedAtMilliseconds < 0
                ? 0
                : checked((int)Math.Max(0, Math.Min(
                    TimedDwmTestRunner.DurationMilliseconds + 1000L,
                    clock.ElapsedMilliseconds - activationObservedAtMilliseconds)));
            var reset = Recover(
                request.CorrelationToken,
                request.WorkerIdentityPath,
                request.RecoveryResponsePath,
                0,
                "Unabhängiger Watchdog",
                verifiedDurationMilliseconds);
            return reset;
        }

        private static DwmPatchResponse RunSupervisedPersistentApply(DwmPatchRequest outerRequest)
        {
            if (outerRequest == null) throw new ArgumentNullException(nameof(outerRequest));
            // Recheck after UAC, before even starting an elevated mutation worker.
            WindowsEnvironmentGuard.RequireCurrent(outerRequest.ExpectedWindowsEnvironment);
            TimedDwmTestRunner.ValidateSettings(outerRequest.Settings);
            if (!TimedDwmTestRunner.IsValidCorrelationToken(outerRequest.CorrelationToken))
                throw new InvalidDataException("Der überwachte Apply-Token ist ungültig.");

            var paths = CreateSupervisedApplyPaths(outerRequest.CorrelationToken);
            ValidateExpectedPath(outerRequest.ActivationPath, paths.Activation, "Aktivierung");
            ValidateExpectedPath(outerRequest.CommitPath, paths.Commit, "Commit");
            ValidateExpectedPath(outerRequest.CommitAcceptedPath, paths.CommitAccepted, "Commit-Bestätigung");
            ValidateExpectedPath(outerRequest.RecoveryResponsePath, paths.RecoveryResponse, "Recovery");
            ValidateStartupApprovalProof(outerRequest);

            using (var supervisorMutex = new Mutex(false, SupervisedMutationMutexName))
            {
                var ownsMutex = TryAcquire(supervisorMutex, 0);
                if (!ownsMutex)
                    throw new InvalidOperationException("Eine andere überwachte DWM-Operation läuft bereits.");
                try
                {
                    return RunSupervisedPersistentApplyCore(
                        outerRequest.Settings, paths, outerRequest.ExpectedWindowsEnvironment, outerRequest.StartupApproval);
                }
                finally
                {
                    supervisorMutex.ReleaseMutex();
                }
            }
        }

        private static DwmPatchResponse RunSupervisedPersistentApplyCore(
            HDRCalibrate.Core.CalibrationSettings settings,
            SupervisedApplyPaths paths,
            WindowsEnvironmentIdentity expectedWindowsEnvironment,
            StartupApprovalFileProof startupApproval)
        {
            Process watchdog = null;
            Process worker = null;
            TimedTestProcessIdentity watchdogIdentity = null;
            TimedTestProcessIdentity workerIdentity = null;
            var helperPath = GetCurrentExecutablePath();
            var supervisorClock = new Stopwatch();
            var fallbackDeadline = PersistentApplySetupDeadlineMilliseconds;
            DwmPatchResponse applied = null;
            DwmPatchResponse committed = null;
            DwmPatchResponse reset = null;
            Exception applyFailure = null;
            var watchdogArmed = false;
            var outcomeFinalized = false;

            try
            {
                WriteRequest(paths.WatchdogRequest, new DwmPatchRequest
                {
                    Command = DwmPatchCommand.SupervisedApplyWatchdog,
                    CorrelationToken = paths.Token,
                    ReadyPath = paths.WatchdogReady,
                    StartGatePath = paths.StartGate,
                    WorkerIdentityPath = paths.WorkerIdentity,
                    WorkerResponsePath = paths.WorkerResponse,
                    ActivationPath = paths.Activation,
                    RecoveryResponsePath = paths.RecoveryResponse,
                    CommitPath = paths.Commit,
                    CommitAcceptedPath = paths.CommitAccepted
                });
                WriteRequest(paths.WorkerRequest, new DwmPatchRequest
                {
                    Command = DwmPatchCommand.SupervisedApplyWorker,
                    Settings = settings.Clone(),
                    ExpectedWindowsEnvironment = expectedWindowsEnvironment.Clone(),
                    StartupApproval = startupApproval,
                    CorrelationToken = paths.Token,
                    StartGatePath = paths.StartGate,
                    WorkerIdentityPath = paths.WorkerIdentity,
                    WorkerResponsePath = paths.WorkerResponse,
                    ActivationPath = paths.Activation,
                    RecoveryResponsePath = paths.RecoveryResponse,
                    CommitPath = paths.Commit,
                    CommitAcceptedPath = paths.CommitAccepted
                });

                watchdog = StartChild(helperPath, paths.WatchdogRequest, paths.WatchdogStatus);
                watchdogIdentity = GetProcessIdentity(watchdog);
                WaitForSuccessfulResponse(
                    paths.WatchdogReady,
                    ChildReadyTimeoutMilliseconds,
                    "Der unabhängige Apply-Watchdog wurde nicht rechtzeitig bereit.");

                worker = StartChild(helperPath, paths.WorkerRequest, paths.WorkerResponse);
                workerIdentity = GetProcessIdentity(worker);
                WriteIdentity(paths.WorkerIdentity, workerIdentity);
                WriteResponse(paths.StartGate, new DwmPatchResponse
                {
                    Success = true,
                    WatchdogArmed = true,
                    CorrelationToken = paths.Token,
                    Message = "Startsignal für das überwachte dauerhafte Anwenden."
                });
                watchdogArmed = true;
                supervisorClock.Start();

                while (supervisorClock.ElapsedMilliseconds < PersistentApplyAbsoluteDeadlineMilliseconds)
                {
                    if (TryReadResponse(paths.Activation, out var activation) && activation.Success)
                    {
                        if (applied == null)
                            fallbackDeadline = checked((int)supervisorClock.ElapsedMilliseconds
                                + PersistentApplyCommitDeadlineMilliseconds + 1000);
                        applied = activation;
                    }
                    if (TryReadResponse(paths.WorkerResponse, out var workerResponse))
                    {
                        if (workerResponse.Success) applied = workerResponse;
                        else applyFailure = new InvalidOperationException(workerResponse.Message);
                    }
                    if (TryReadResponse(paths.CommitAccepted, out var accepted) && accepted.Success)
                    {
                        committed = accepted;
                        break;
                    }
                    if (TryReadResponse(paths.RecoveryResponse, out var recovery) && recovery.Success)
                    {
                        reset = recovery;
                        break;
                    }

                    if (watchdog.HasExited || supervisorClock.ElapsedMilliseconds >= fallbackDeadline)
                    {
                        reset = RecoverOrTakeOver(
                            paths.Token,
                            paths,
                            workerIdentity,
                            watchdog,
                            watchdogIdentity,
                            watchdog.HasExited
                                ? "Apply-Watchdog endete vor einer bestätigten Commit-Entscheidung."
                                : "Apply-Supervisor-Sicherheitsfrist erreicht.");
                        break;
                    }
                    Thread.Sleep(10);
                }

                if (committed != null)
                {
                    SupervisedApplyProtocol.ValidateCommittedApply(paths.Token, applied, committed);
                    var identity = new TimedTestProcessIdentity
                    {
                        ProcessId = committed.DwmProcessId,
                        StartTimeUtcTicks = committed.DwmStartTimeUtcTicks
                    };
                    if (!IsExactProcessAlive(identity))
                        throw new InvalidOperationException("Der bestätigte DWM-Prozess läuft nicht mehr.");

                    if (watchdog != null && !watchdog.HasExited) watchdog.WaitForExit(2000);
                    if (watchdog != null && !watchdog.HasExited)
                        throw new InvalidOperationException("Der Apply-Watchdog wurde nach dem Commit nicht beendet.");

                    applied.WatchdogArmed = true;
                    applied.CommitAccepted = true;
                    applied.CorrelationToken = paths.Token;
                    applied.Message = "DWM-SDR-Korrektur überwacht angewendet; Zustands-Commit bestätigt.";
                    outcomeFinalized = true;
                    return applied;
                }

                if (reset == null)
                    reset = RecoverOrTakeOver(
                        paths.Token,
                        paths,
                        workerIdentity,
                        watchdog,
                        watchdogIdentity,
                        "Das überwachte Anwenden überschritt die absolute Sicherheitsfrist.");

                var failureResponse = CreateRecoveredApplyFailure(paths.Token, reset, applyFailure);
                outcomeFinalized = true;
                return failureResponse;
            }
            catch (Exception supervisorFailure)
            {
                if (!watchdogArmed || outcomeFinalized) throw;

                try
                {
                    reset = RecoverOrTakeOver(
                        paths.Token,
                        paths,
                        workerIdentity,
                        watchdog,
                        watchdogIdentity,
                        "Der Apply-Supervisor ist vor einer sicheren Abschlussentscheidung fehlgeschlagen.");
                    var failureResponse = CreateRecoveredApplyFailure(paths.Token, reset, supervisorFailure);
                    outcomeFinalized = true;
                    return failureResponse;
                }
                catch (Exception recoveryFailure)
                {
                    // Do not terminate the independently running watchdog or remove its IPC
                    // files. It is the remaining authority that can still reset a patched DWM.
                    throw new AggregateException(
                        "Der Apply-Supervisor ist fehlgeschlagen; der unabhängige Watchdog bleibt für die Rücksetzung aktiv.",
                        supervisorFailure,
                        recoveryFailure);
                }
            }
            finally
            {
                TryTerminateExact(workerIdentity);
                if (!watchdogArmed || outcomeFinalized) TryTerminateExact(watchdogIdentity);
                if (worker != null) worker.Dispose();
                if (watchdog != null) watchdog.Dispose();
                if (!watchdogArmed || outcomeFinalized)
                    foreach (var path in paths.All) TryDelete(path);
            }
        }

        private static DwmPatchResponse CreateRecoveredApplyFailure(
            string token,
            DwmPatchResponse reset,
            Exception failure)
        {
            if (reset == null || !reset.Success)
                throw new InvalidDataException("Die überwachte Apply-Rücksetzung wurde nicht bestätigt.");
            return new DwmPatchResponse
            {
                Success = false,
                WatchdogArmed = true,
                RecoveryPerformed = true,
                WatchdogResetUtcTicks = reset.WatchdogResetUtcTicks,
                DwmProcessId = reset.DwmProcessId,
                DwmStartTimeUtcTicks = reset.DwmStartTimeUtcTicks,
                DwmCoreVersion = reset.DwmCoreVersion,
                CorrelationToken = token,
                Message = "Das dauerhafte Anwenden wurde nicht bestätigt; DWM wurde sicher ungepatcht neu gestartet. " +
                    (failure == null ? string.Empty : failure.Message),
                Diagnostics = reset.Diagnostics ?? new List<string>()
            };
        }

        private static DwmPatchResponse RunSupervisedPersistentWorker(
            DwmPatchRequest request,
            string responsePath)
        {
            ValidateStartupApprovalProof(request);
            TimedDwmTestRunner.ValidateSettings(request.Settings);
            WaitForSuccessfulResponse(
                request.StartGatePath,
                ChildReadyTimeoutMilliseconds,
                "Das Startsignal des überwachten Anwendens fehlt.");

            var response = new DwmPatchEngine().ApplySupervisedPersistent(request.Settings, activation =>
            {
                activation.WatchdogArmed = true;
                activation.CorrelationToken = request.CorrelationToken;
                WriteResponse(request.ActivationPath, activation);
            }, request.ExpectedWindowsEnvironment, request.StartupApproval);
            response.WatchdogArmed = true;
            response.CorrelationToken = request.CorrelationToken;
            WriteResponse(responsePath, response);

            // The watchdog terminates this exact process after commit or before recovery.
            // Holding the operation mutex prevents every competing DWM mutation meanwhile.
            Thread.Sleep(Timeout.Infinite);
            throw new InvalidOperationException("Der überwachte Apply-Worker wurde unerwartet fortgesetzt.");
        }

        private static void ValidateStartupApprovalProof(DwmPatchRequest request)
        {
            if (request.StartupApproval == null) return; // Explicit manual apply has no startup policy.
            var activationPath = ValidateIpcPath(request.ActivationPath);
            var expectedPolicyPath = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(activationPath), "..", "dwm-startup-policy.xml"));
            if (!string.Equals(
                Path.GetFullPath(request.StartupApproval.PolicyPath), expectedPolicyPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Der Startfreigabenachweis stammt nicht aus dem Anwendungsdatenverzeichnis.");
            request.StartupApproval.RequireUnchanged();
        }

        private static DwmPatchResponse RunSupervisedPersistentWatchdog(DwmPatchRequest request)
        {
            WriteResponse(request.ReadyPath, new DwmPatchResponse
            {
                Success = true,
                WatchdogArmed = true,
                CorrelationToken = request.CorrelationToken,
                Message = "Unabhängiger Apply-Watchdog ist bereit."
            });
            WaitForSuccessfulResponse(
                request.StartGatePath,
                ChildReadyTimeoutMilliseconds * 2,
                "Der Apply-Watchdog erhielt kein Startsignal.");

            var clock = Stopwatch.StartNew();
            var deadline = PersistentApplySetupDeadlineMilliseconds;
            DwmPatchResponse activation = null;
            DwmPatchResponse worker = null;
            while (clock.ElapsedMilliseconds < deadline)
            {
                if (TryReadResponse(request.ActivationPath, out var observedActivation)
                    && observedActivation.Success)
                {
                    if (activation == null)
                        deadline = checked((int)clock.ElapsedMilliseconds
                            + PersistentApplyCommitDeadlineMilliseconds);
                    activation = observedActivation;
                }
                if (TryReadResponse(request.WorkerResponsePath, out var observedWorker))
                {
                    if (!observedWorker.Success) break;
                    worker = observedWorker;
                }
                if (activation != null
                    && worker != null
                    && TryReadResponse(request.CommitPath, out var commit)
                    && SupervisedApplyProtocol.IsValidCommit(request.CorrelationToken, activation, worker, commit))
                {
                    var dwmIdentity = new TimedTestProcessIdentity
                    {
                        ProcessId = worker.DwmProcessId,
                        StartTimeUtcTicks = worker.DwmStartTimeUtcTicks
                    };
                    if (!IsExactProcessAlive(dwmIdentity)) break;
                    if (TryReadIdentity(request.WorkerIdentityPath, out var workerIdentity))
                        TryTerminateExact(workerIdentity);

                    var accepted = CopyApplyResponse(worker);
                    accepted.Success = true;
                    accepted.WatchdogArmed = true;
                    accepted.CommitAccepted = true;
                    accepted.CorrelationToken = request.CorrelationToken;
                    accepted.Message = "Der unabhängige Watchdog hat den Zustands-Commit bestätigt.";
                    WriteResponse(request.CommitAcceptedPath, accepted);
                    return accepted;
                }
                if (File.Exists(request.WorkerIdentityPath)
                    && TryReadIdentity(request.WorkerIdentityPath, out var observedWorkerIdentity)
                    && !IsExactProcessAlive(observedWorkerIdentity))
                    break;
                Thread.Sleep(5);
            }

            return Recover(
                request.CorrelationToken,
                request.WorkerIdentityPath,
                request.RecoveryResponsePath,
                0,
                "Unabhängiger Apply-Watchdog");
        }

        private static DwmPatchResponse CopyApplyResponse(DwmPatchResponse source)
        {
            return new DwmPatchResponse
            {
                Success = source.Success,
                DwmProcessId = source.DwmProcessId,
                DwmStartTimeUtcTicks = source.DwmStartTimeUtcTicks,
                DwmCoreVersion = source.DwmCoreVersion,
                PatchedShaderCount = source.PatchedShaderCount,
                PatchedConstantVectorCount = source.PatchedConstantVectorCount,
                ChangedByteCount = source.ChangedByteCount,
                ModifiedPageCount = source.ModifiedPageCount,
                PatchActivatedUtcTicks = source.PatchActivatedUtcTicks,
                PatchPlanFingerprint = source.PatchPlanFingerprint,
                Diagnostics = source.Diagnostics == null
                    ? new List<string>()
                    : new List<string>(source.Diagnostics)
            };
        }

        private static DwmPatchResponse RecoverOrTakeOver(
            string token,
            TimedTestPaths paths,
            TimedTestProcessIdentity workerIdentity,
            Process watchdog,
            TimedTestProcessIdentity watchdogIdentity,
            string reason)
        {
            return RecoverOrTakeOverCore(
                token,
                paths.WorkerIdentity,
                paths.RecoveryResponse,
                workerIdentity,
                watchdog,
                watchdogIdentity,
                reason);
        }

        private static DwmPatchResponse RecoverOrTakeOver(
            string token,
            SupervisedApplyPaths paths,
            TimedTestProcessIdentity workerIdentity,
            Process watchdog,
            TimedTestProcessIdentity watchdogIdentity,
            string reason)
        {
            return RecoverOrTakeOverCore(
                token,
                paths.WorkerIdentity,
                paths.RecoveryResponse,
                workerIdentity,
                watchdog,
                watchdogIdentity,
                reason);
        }

        private static DwmPatchResponse RecoverOrTakeOverCore(
            string token,
            string workerIdentityPath,
            string recoveryResponsePath,
            TimedTestProcessIdentity workerIdentity,
            Process watchdog,
            TimedTestProcessIdentity watchdogIdentity,
            string reason)
        {
            var deadline = Stopwatch.StartNew();
            while (deadline.ElapsedMilliseconds < 1000)
            {
                if (TryReadResponse(recoveryResponsePath, out var existing) && existing.Success)
                    return existing;
                Thread.Sleep(10);
            }

            try
            {
                return Recover(token, workerIdentityPath, recoveryResponsePath, 250, reason);
            }
            catch (TimeoutException)
            {
                while (deadline.ElapsedMilliseconds < 17000)
                {
                    if (TryReadResponse(recoveryResponsePath, out var existing) && existing.Success)
                        return existing;
                    if (watchdog != null && watchdog.HasExited) break;
                    Thread.Sleep(20);
                }

                TryTerminateExact(watchdogIdentity);
                TryTerminateExact(workerIdentity);
                return Recover(token, workerIdentityPath, recoveryResponsePath, 2000,
                    reason + " Der nicht antwortende Watchdog wurde übernommen.");
            }
        }

        private static DwmPatchResponse Recover(
            string token,
            string workerIdentityPath,
            string recoveryResponsePath,
            int leaderTimeoutMilliseconds,
            string actor,
            int verifiedTestDurationMilliseconds = 0)
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
                    reset.TestDurationMilliseconds = verifiedTestDurationMilliseconds;
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

        private static void EnsureNoSupervisedMutationActive()
        {
            using (var mutex = new Mutex(false, SupervisedMutationMutexName))
            {
                if (!TryAcquire(mutex, 0))
                    throw new InvalidOperationException("Während einer überwachten DWM-Operation ist keine zweite DWM-Operation zulässig.");
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
            if (request.Command == DwmPatchCommand.SupervisedApplyWorker
                || request.Command == DwmPatchCommand.SupervisedApplyWatchdog)
                paths = paths.Concat(new[]
                {
                    request.CommitPath,
                    request.CommitAcceptedPath
                }).ToArray();
            if (request.Command == DwmPatchCommand.TimedTestWatchdog
                || request.Command == DwmPatchCommand.SupervisedApplyWatchdog)
                paths = paths.Concat(new[] { request.ReadyPath }).ToArray();
            foreach (var path in paths)
            {
                var validated = ValidateIpcPath(path);
                if (Path.GetFileName(validated).IndexOf(request.CorrelationToken, StringComparison.OrdinalIgnoreCase) < 0)
                    throw new UnauthorizedAccessException("Eine interne IPC-Datei gehört nicht zum Test-Token.");
            }
        }

        private static void ValidateExpectedPath(string supplied, string expected, string label)
        {
            var validated = ValidateIpcPath(supplied);
            if (!string.Equals(validated, expected, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException(
                    "Der IPC-Pfad für " + label + " entspricht nicht dem überwachten Apply-Token.");
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

        private static SupervisedApplyPaths CreateSupervisedApplyPaths(string token)
        {
            var root = GetRequestRoot();
            Directory.CreateDirectory(root);
            return new SupervisedApplyPaths(root, token);
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

        private sealed class SupervisedApplyPaths
        {
            public SupervisedApplyPaths(string root, string token)
            {
                Token = token;
                WatchdogRequest = Make(root, "apply-watchdog-request", token);
                WatchdogStatus = Make(root, "apply-watchdog-status", token);
                WatchdogReady = Make(root, "apply-watchdog-ready", token);
                WorkerRequest = Make(root, "apply-worker-request", token);
                WorkerResponse = Make(root, "apply-worker-response", token);
                WorkerIdentity = Make(root, "apply-worker-identity", token);
                StartGate = Make(root, "apply-start-gate", token);
                Activation = Make(root, "apply-activation", token);
                Commit = Make(root, "apply-commit", token);
                CommitAccepted = Make(root, "apply-commit-accepted", token);
                RecoveryResponse = Make(root, "apply-recovery", token);
                All = new[]
                {
                    WatchdogRequest, WatchdogStatus, WatchdogReady, WorkerRequest,
                    WorkerResponse, WorkerIdentity, StartGate, Activation, Commit,
                    CommitAccepted, RecoveryResponse
                };
            }

            public string Token { get; }
            public string WatchdogRequest { get; }
            public string WatchdogStatus { get; }
            public string WatchdogReady { get; }
            public string WorkerRequest { get; }
            public string WorkerResponse { get; }
            public string WorkerIdentity { get; }
            public string StartGate { get; }
            public string Activation { get; }
            public string Commit { get; }
            public string CommitAccepted { get; }
            public string RecoveryResponse { get; }
            public IEnumerable<string> All { get; }

            private static string Make(string root, string prefix, string token)
            {
                return Path.Combine(root, prefix + "-" + token + ".xml");
            }
        }
    }
}
