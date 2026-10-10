using HDRCalibrate.Core;
using HDRCalibrate.Dwm;
using HDRCalibrate.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace HDRCalibrate.Tests
{
    internal static class ProgramGammaControllerTests
    {
        private const string ExePath = @"C:\Games\Any Application\game.exe";
        private static readonly long StartTicks = new DateTime(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc).Ticks;

        private sealed class RunCall
        {
            public ProgramGammaTarget Target;
            public ProgramGammaApproval Approval;
            public bool Continuous;
            public Func<bool> Authorize;
            public Action<ProgramGammaNativeEvent> Notify;
            public CancellationToken Token;
        }

        private sealed class FakeRunner : IProgramGammaSessionRunner
        {
            private readonly object _gate = new object();
            private readonly List<RunCall> _calls = new List<RunCall>();
            public Func<RunCall, Task<ProgramGammaRunResult>> Behavior;
            public int Count { get { lock (_gate) return _calls.Count; } }
            public RunCall Call(int index) { lock (_gate) return _calls[index]; }

            public Task<ProgramGammaRunResult> RunAsync(ProgramGammaTarget target, ProgramGammaApproval approval,
                bool continuous, Func<bool> authorizeStart, Action<ProgramGammaNativeEvent> onEvent,
                CancellationToken cancellationToken)
            {
                var call = new RunCall
                {
                    Target = target, Approval = approval, Continuous = continuous,
                    Authorize = authorizeStart, Notify = onEvent, Token = cancellationToken
                };
                lock (_gate) _calls.Add(call);
                if (Behavior != null) return Behavior(call);
                if (!call.Authorize()) throw new InvalidOperationException("Fake helper refused an invalid start.");
                call.Notify(ActiveTelemetry());
                return Task.FromResult(Success());
            }
        }

        private sealed class Fixture : IDisposable
        {
            public ProgramGammaTarget Target = TargetIdentity();
            public ProgramGammaApproval Approval = CreateApproval();
            public readonly FakeRunner Runner = new FakeRunner();
            public readonly ProgramGammaController Controller;
            public int ApprovalReads;
            public int FinderReads;
            public Func<ProgramGammaApproval> ReadApproval;

            public Fixture()
            {
                Controller = new ProgramGammaController(() =>
                {
                    Interlocked.Increment(ref ApprovalReads);
                    return ReadApproval == null ? Approval : ReadApproval();
                }, path =>
                {
                    Interlocked.Increment(ref FinderReads);
                    Check(string.Equals(path, ExePath, StringComparison.OrdinalIgnoreCase), "Finder received a different requested executable.");
                    return Target;
                }, Runner);
            }

            public void Dispose() => Controller.Dispose();
        }

        private static ProgramGammaTarget TargetIdentity(int pid = 4567, long? creation = null,
            long hwnd = 65538, string path = ExePath, string title = "Independent SDR program")
            => new ProgramGammaTarget(path, pid, creation ?? ProgramGammaApproval.ToCreationFileTime(StartTicks), hwnd, title);

        private static WindowsEnvironmentIdentity EnvironmentIdentity() => new WindowsEnvironmentIdentity
        {
            OsBuildNumber = 26100, UpdateBuildRevision = 8875,
            BuildLabEx = "26100.1.amd64fre.ge_release.240331-1435",
            DwmExecutableSha256 = new string('B', 64),
            DwmCoreSha256 = ProgramGammaBuildSupport.SupportedDwmCoreSha256
        };

        private static ProgramGammaApproval CreateApproval(double gamma = 2.4, double brightness = 0.03,
            int dwmPid = 1234, long? startTicks = null, string plan = null,
            WindowsEnvironmentIdentity environment = null, Func<bool> live = null, int sessionId = 2,
            string dwmCoreVersion = ProgramGammaBuildSupport.SupportedDwmCoreVersion)
        {
            var response = new DwmPatchResponse
            {
                Success = true, WatchdogArmed = true, CommitAccepted = true,
                CorrelationToken = "0123456789abcdef0123456789abcdef",
                DwmProcessId = dwmPid, DwmStartTimeUtcTicks = startTicks ?? StartTicks,
                PatchActivatedUtcTicks = StartTicks + TimeSpan.TicksPerSecond,
                DwmCoreVersion = dwmCoreVersion,
                PatchPlanFingerprint = plan ?? new string('A', 64),
                PatchedShaderCount = 4, PatchedConstantVectorCount = 16, ChangedByteCount = 64
            };
            var state = new PersistedDwmState
            {
                WasApplied = true, SessionId = sessionId,
                DwmProcessId = response.DwmProcessId, DwmStartTimeUtcTicks = response.DwmStartTimeUtcTicks,
                DwmCoreVersion = response.DwmCoreVersion, PatchPlanFingerprint = response.PatchPlanFingerprint,
                Settings = new CalibrationSettings { ApplySdrGammaCorrection = true, Gamma = gamma, Brightness = brightness }
            };
            var windows = environment ?? EnvironmentIdentity();
            return ProgramGammaApproval.CreateFromCommittedApply(response, state, windows, windows.Clone(), live ?? (() => true));
        }

        private static ProgramGammaNativeEvent Complete() => new ProgramGammaNativeEvent
        {
            Event = "complete", Started = true, Armed = true, TargetRetained = true, DwmRetained = true,
            StopCompleted = true, StopStatus = 0, VetoedCandidates = 120,
            ReadFaults = 0, ActiveCallbacks = 0, NoPostExpiryVeto = true
        };

        private static ProgramGammaNativeEvent ActiveTelemetry()
        {
            var now = Stopwatch.GetTimestamp();
            return new ProgramGammaNativeEvent
            {
                Event = "telemetry", Phase = "veto", Vetoed = 1, ReadFaults = 0,
                SampleValid = true, Point = 1, Decision = 1, ColorSpace = 0, PixelFormat = 87,
                Qpc = now, SampleQpc = now, OwnerPid = 4567, WindowHandle = 65538
            };
        }

        private static ProgramGammaRunResult Success() => new ProgramGammaRunResult { ExitCode = 0, Completion = Complete() };

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void Await(Task task)
        {
            if (Task.WhenAny(task, Task.Delay(5000)).GetAwaiter().GetResult() != task)
                throw new TimeoutException("An offline controller operation did not complete.");
            task.GetAwaiter().GetResult();
        }

        private static void Reject(Func<Task> action)
        {
            try { Await(action()); }
            catch (InvalidOperationException) { return; }
            catch (ArgumentException) { return; }
            catch (NotSupportedException) { return; }
            throw new InvalidOperationException("The controller accepted an operation without valid authorization.");
        }

        private static void Idle(Fixture fixture)
        {
            Check(!fixture.Controller.IsBusy && !fixture.Controller.IsActive
                && fixture.Controller.ActiveExecutablePath == null, "Finished operation retained an active/busy session.");
        }

        private static bool Authorized(RunCall call)
        {
            try { return call.Authorize(); }
            catch (OperationCanceledException) { return false; }
        }

        private static RunCall WaitForCall(FakeRunner runner, int count)
        {
            if (!SpinWait.SpinUntil(() => runner.Count >= count, 5000))
                throw new InvalidOperationException("Fake runner was not reached.");
            return runner.Call(count - 1);
        }

        public static void ApprovalAndManualActivationGates()
        {
            using (var fixture = new Fixture())
            {
                Check(fixture.Runner.Count == 0 && fixture.ApprovalReads == 0 && fixture.FinderReads == 0,
                    "Creating a controller automatically inspected or activated a program.");
                Reject(() => fixture.Controller.ActivateAsync(ExePath));
                Check(fixture.Runner.Count == 0, "Activation without a test reached the helper.");
                Idle(fixture);
            }
            foreach (var missing in new Func<ProgramGammaApproval>[]
            {
                () => null,
                () => { throw new InvalidOperationException("No committed apply in this app instance."); },
                () => CreateApproval(live: () => false)
            })
            {
                using (var fixture = new Fixture())
                {
                    fixture.ReadApproval = missing;
                    Reject(() => fixture.Controller.TestAsync(ExePath));
                    Check(fixture.Runner.Count == 0 && fixture.FinderReads == 0,
                        "Missing active apply evidence reached a target finder or helper.");
                    Idle(fixture);
                }
            }
        }

        public static void SuccessfulTestEnablesExactActivation()
        {
            using (var fixture = new Fixture())
            {
                var becameActive = false;
                fixture.Controller.StateChanged += (sender, args) => becameActive |= fixture.Controller.IsActive;
                Await(fixture.Controller.TestAsync(ExePath));
                Check(fixture.Runner.Count == 1 && !fixture.Runner.Call(0).Continuous && !becameActive,
                    "A bounded test requested automatic continuous activation.");
                Idle(fixture);
                Await(fixture.Controller.ActivateAsync(ExePath));
                Check(fixture.Runner.Count == 2 && fixture.Runner.Call(1).Continuous && becameActive,
                    "The exact completed test did not allow one explicit activation.");
                Check(ReferenceEquals(fixture.Runner.Call(0).Approval, fixture.Approval)
                    && fixture.Runner.Call(1).Target.IdentityKey == fixture.Target.IdentityKey,
                    "Runner did not receive the approved gamma/target snapshot.");
                Idle(fixture);
                Reject(() => fixture.Controller.ActivateAsync(ExePath));
                Check(fixture.Runner.Count == 2, "A finished continuous session automatically retained approval for another activation.");
            }
        }

        public static void IncompleteCompletionsNeverApprove()
        {
            foreach (var corrupt in new Action<ProgramGammaRunResult>[]
            {
                result => result.ExitCode = 20,
                result => result.Failure = "The source changed after arming.",
                result => result.Completion = null,
                result => result.Completion.Event = "telemetry",
                result => result.Completion.Started = null,
                result => result.Completion.Started = false,
                result => result.Completion.Armed = null,
                result => result.Completion.Armed = false,
                result => result.Completion.TargetRetained = false,
                result => result.Completion.DwmRetained = null,
                result => result.Completion.StopCompleted = false,
                result => result.Completion.StopStatus = 5,
                result => result.Completion.VetoedCandidates = 0,
                result => result.Completion.VetoedCandidates = null,
                result => result.Completion.ReadFaults = 1,
                result => result.Completion.ReadFaults = null,
                result => result.Completion.ActiveCallbacks = 1,
                result => result.Completion.NoPostExpiryVeto = false
            })
            {
                using (var fixture = new Fixture())
                {
                    fixture.Runner.Behavior = call =>
                    {
                        Check(call.Authorize(), "Valid test authorization was unexpectedly rejected.");
                        var result = Success();
                        corrupt(result);
                        return Task.FromResult(result);
                    };
                    Reject(() => fixture.Controller.TestAsync(ExePath));
                    Idle(fixture);
                    Reject(() => fixture.Controller.ActivateAsync(ExePath));
                    Check(fixture.Runner.Count == 1, "Incomplete completion created an activation proof.");
                }
            }
        }

        public static void IdentityAndSettingsChangesRejectProof()
        {
            foreach (var mutate in IdentityMutations())
            {
                using (var fixture = new Fixture())
                {
                    Await(fixture.Controller.TestAsync(ExePath));
                    mutate(fixture);
                    Reject(() => fixture.Controller.ActivateAsync(ExePath));
                    Check(fixture.Runner.Count == 1, "Changed identity/settings reused a previous test proof.");
                    Idle(fixture);
                }
            }
            using (var fixture = new Fixture())
            {
                Await(fixture.Controller.TestAsync(ExePath));
                fixture.Target = TargetIdentity(title: "Only the display title changed");
                Await(fixture.Controller.ActivateAsync(ExePath));
                Check(fixture.Runner.Count == 2, "A title-only change was confused with a target identity change.");
            }
        }

        private static IEnumerable<Action<Fixture>> IdentityMutations()
        {
            yield return fixture => fixture.Target = TargetIdentity(pid: 4568);
            yield return fixture => fixture.Target = TargetIdentity(creation: fixture.Target.CreationFileTime + 1);
            yield return fixture => fixture.Target = TargetIdentity(hwnd: 65539);
            yield return fixture => fixture.Target = TargetIdentity(path: @"C:\Games\Other Application\game.exe");
            yield return fixture => fixture.Approval = CreateApproval(gamma: 2.2);
            yield return fixture => fixture.Approval = CreateApproval(brightness: 0.04);
            yield return fixture => fixture.Approval = CreateApproval(dwmPid: 1235);
            yield return fixture => fixture.Approval = CreateApproval(startTicks: StartTicks + 1);
            yield return fixture => fixture.Approval = CreateApproval(sessionId: 3);
            yield return fixture => fixture.Approval = CreateApproval(plan: new string('C', 64));
            yield return fixture => fixture.Approval = CreateApproval(live: () => false);
        }

        public static void ChangesDuringTestOrAuthorizationRejectProof()
        {
            foreach (var mutate in IdentityMutations())
            {
                using (var fixture = new Fixture())
                {
                    fixture.Runner.Behavior = call =>
                    {
                        Check(call.Authorize(), "Valid initial authorization was rejected.");
                        mutate(fixture);
                        return Task.FromResult(Success());
                    };
                    Reject(() => fixture.Controller.TestAsync(ExePath));
                    Reject(() => fixture.Controller.ActivateAsync(ExePath));
                    Check(fixture.Runner.Count == 1, "Identity change during the test produced usable proof.");
                }
                using (var fixture = new Fixture())
                {
                    Await(fixture.Controller.TestAsync(ExePath));
                    var denied = false;
                    fixture.Runner.Behavior = call =>
                    {
                        mutate(fixture);
                        denied = !Authorized(call);
                        if (denied) throw new InvalidOperationException("The fake UAC boundary denied changed evidence.");
                        return Task.FromResult(Success());
                    };
                    Reject(() => fixture.Controller.ActivateAsync(ExePath));
                    Check(denied, "Identity changed after preparation but was authorized for remote access.");
                    Idle(fixture);
                }
            }
        }

        public static void StopDisposeAndSingleSession()
        {
            foreach (var continuous in new[] { false, true })
            {
                using (var fixture = new Fixture())
                {
                    if (continuous) Await(fixture.Controller.TestAsync(ExePath));
                    var completed = new TaskCompletionSource<ProgramGammaRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                    fixture.Runner.Behavior = ignored => completed.Task;
                    var running = continuous ? fixture.Controller.ActivateAsync(ExePath) : fixture.Controller.TestAsync(ExePath);
                    var expectedCalls = continuous ? 2 : 1;
                    var call = WaitForCall(fixture.Runner, expectedCalls);
                    try
                    {
                        Check(Authorized(call), "Prepared session was not authorized.");
                        call.Notify(ActiveTelemetry());
                        Check(fixture.Controller.IsActive == continuous, "Continuous activity state was incorrect.");
                        Reject(() => fixture.Controller.TestAsync(ExePath));
                        Reject(() => fixture.Controller.ActivateAsync(ExePath));
                        Check(fixture.Runner.Count == expectedCalls, "A second concurrent native session started.");
                        fixture.Controller.Stop();
                        Check(call.Token.IsCancellationRequested && !Authorized(call), "Stop allowed further lease/start authorization.");
                        call.Notify(ActiveTelemetry());
                        Check(!fixture.Controller.IsActive, "Late telemetry reactivated a stopped session.");
                    }
                    finally { completed.TrySetResult(Success()); }
                    Await(running);
                    Idle(fixture);
                    Reject(() => fixture.Controller.ActivateAsync(ExePath));
                    Check(fixture.Runner.Count == expectedCalls, "Stop retained an activation proof.");
                }
            }
            using (var fixture = new Fixture())
            {
                var completed = new TaskCompletionSource<ProgramGammaRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                fixture.Runner.Behavior = ignored => completed.Task;
                var running = fixture.Controller.TestAsync(ExePath);
                var call = WaitForCall(fixture.Runner, 1);
                fixture.Controller.Dispose();
                Check(call.Token.IsCancellationRequested && !Authorized(call), "Dispose allowed further authorization.");
                completed.SetResult(Success());
                Await(running);
                Reject(() => fixture.Controller.TestAsync(ExePath));
                Reject(() => fixture.Controller.ActivateAsync(ExePath));
                Check(fixture.Runner.Count == 1, "Disposed controller started a new helper.");
                Idle(fixture);
            }
            using (var fixture = new Fixture())
            {
                Await(fixture.Controller.TestAsync(ExePath));
                fixture.Controller.Stop();
                Reject(() => fixture.Controller.ActivateAsync(ExePath));
                Check(fixture.Runner.Count == 1, "Stop on an idle controller failed to revoke the completed test.");
            }
        }

        public static void StopDuringFinalValidationCannotRestoreProof()
        {
            using (var fixture = new Fixture())
            {
                fixture.ReadApproval = () =>
                {
                    // Initial check, UAC-bound authorization, final test validation.
                    if (fixture.ApprovalReads == 3) fixture.Controller.Stop();
                    return fixture.Approval;
                };
                Await(fixture.Controller.TestAsync(ExePath));
                Check(fixture.ApprovalReads == 3, "The cancellation race was not exercised at final validation.");
                Reject(() => fixture.Controller.ActivateAsync(ExePath));
                Check(fixture.Runner.Count == 1, "Final validation restored the proof after Stop revoked it.");
                Idle(fixture);
            }
        }

        public static void NativeWithdrawalCannotReactivate()
        {
            foreach (var terminal in new[]
            {
                new ProgramGammaNativeEvent { Event = "managed_surface_rejected" },
                Complete(),
                new ProgramGammaNativeEvent { Event = "telemetry", Phase = "expired" },
                new ProgramGammaNativeEvent { Event = "telemetry", Phase = "recovery" }
            })
            {
                using (var fixture = new Fixture())
                {
                    Await(fixture.Controller.TestAsync(ExePath));
                    var completed = new TaskCompletionSource<ProgramGammaRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                    fixture.Runner.Behavior = ignored => completed.Task;
                    var running = fixture.Controller.ActivateAsync(ExePath);
                    var call = WaitForCall(fixture.Runner, 2);
                    try
                    {
                        call.Notify(ActiveTelemetry());
                        Check(fixture.Controller.IsActive, "Continuous session did not enter activity before terminal event.");
                        call.Notify(terminal);
                        Check(!fixture.Controller.IsActive, "A withdrawn native session still appeared active.");
                        call.Notify(ActiveTelemetry());
                        Check(!fixture.Controller.IsActive, "Later telemetry resurrected a withdrawn native session.");
                    }
                    finally
                    {
                        fixture.Controller.Stop();
                        completed.TrySetResult(Success());
                    }
                    Await(running);
                }
            }
        }

        public static void WindowsChangesRejectProof()
        {
            foreach (var change in new Action<WindowsEnvironmentIdentity>[]
            {
                windows => windows.OsBuildNumber++,
                windows => windows.UpdateBuildRevision++,
                windows => windows.BuildLabEx += "-changed",
                windows => windows.DwmExecutableSha256 = new string('E', 64)
            })
            {
                using (var fixture = new Fixture())
                {
                    Await(fixture.Controller.TestAsync(ExePath));
                    var changedWindows = EnvironmentIdentity();
                    change(changedWindows);
                    fixture.Approval = CreateApproval(environment: changedWindows);
                    Reject(() => fixture.Controller.ActivateAsync(ExePath));
                    Check(fixture.Runner.Count == 1, "Changed Windows identity reused a completed test proof.");
                }
            }
        }

        public static void UnsupportedBuildNeverFindsTargetOrLaunchesHelper()
        {
            foreach (var continuous in new[] { false, true })
            {
                using (var fixture = new Fixture())
                {
                    fixture.ReadApproval = () =>
                    {
                        var updated = EnvironmentIdentity();
                        updated.UpdateBuildRevision = 9278;
                        updated.DwmCoreSha256 = "40579ECFA394395677FEC84014C32BA5495F334B6D96C5653FD5CAA129DC1222";
                        return CreateApproval(environment: updated,
                            dwmCoreVersion: "10.0.26100.9278 (WinBuild.160101.0800)");
                    };
                    Reject(() => continuous ? fixture.Controller.ActivateAsync(ExePath) : fixture.Controller.TestAsync(ExePath));
                    Check(fixture.ApprovalReads == 1 && fixture.FinderReads == 0 && fixture.Runner.Count == 0,
                        "An unsupported private build inspected an application or reached the native helper.");
                    Check(fixture.Controller.Status.Contains(ProgramGammaBuildSupport.UnsupportedMessage),
                        "The controller hid the unsupported Windows-build explanation.");
                    Idle(fixture);
                }
            }
        }
    }
}
