using HDRCalibrate.Core;
using HDRCalibrate.Dwm;
using HDRCalibrate.Services;
using HDRCalibrate.ViewModels;
using System;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;

namespace HDRCalibrate.Tests
{
    internal static class ProgramGammaApprovalTests
    {
        private const string NewGlobalDwmVersion = "10.0.26100.9278 (WinBuild.160101.0800)";
        private const string NewGlobalDwmSha256 = "40579ECFA394395677FEC84014C32BA5495F334B6D96C5653FD5CAA129DC1222";

        private sealed class Evidence
        {
            public DwmPatchResponse Response;
            public PersistedDwmState State;
            public WindowsEnvironmentIdentity Environment;
        }

        private static Evidence ValidEvidence()
        {
            var start = new DateTime(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);
            var response = new DwmPatchResponse
            {
                Success = true,
                WatchdogArmed = true,
                CommitAccepted = true,
                CorrelationToken = "0123456789abcdef0123456789abcdef",
                DwmProcessId = 1234,
                DwmStartTimeUtcTicks = start.Ticks,
                PatchActivatedUtcTicks = start.AddSeconds(2).Ticks,
                DwmCoreVersion = ProgramGammaBuildSupport.SupportedDwmCoreVersion,
                PatchPlanFingerprint = new string('A', 64),
                PatchedShaderCount = 4,
                PatchedConstantVectorCount = 16,
                ChangedByteCount = 64
            };
            return new Evidence
            {
                Response = response,
                State = new PersistedDwmState
                {
                    WasApplied = true,
                    DwmProcessId = response.DwmProcessId,
                    DwmStartTimeUtcTicks = response.DwmStartTimeUtcTicks,
                    SessionId = 2,
                    DwmCoreVersion = response.DwmCoreVersion,
                    PatchPlanFingerprint = response.PatchPlanFingerprint,
                    AppliedUtc = start.AddSeconds(2),
                    Settings = new CalibrationSettings
                    {
                        ApplySdrGammaCorrection = true,
                        Gamma = 2.4,
                        Brightness = 0.03
                    }
                },
                Environment = new WindowsEnvironmentIdentity
                {
                    OsBuildNumber = 26100,
                    UpdateBuildRevision = 8875,
                    BuildLabEx = "26100.1.amd64fre.ge_release.240331-1435",
                    DwmExecutableSha256 = new string('B', 64),
                    DwmCoreSha256 = ProgramGammaBuildSupport.SupportedDwmCoreSha256
                }
            };
        }

        private static ProgramGammaApproval Approve(Evidence evidence, Func<bool> isStillValid = null)
        {
            return ProgramGammaApproval.CreateFromCommittedApply(evidence.Response, evidence.State,
                evidence.Environment, evidence.Environment.Clone(), isStillValid ?? (() => true));
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void Reject(Action action)
        {
            try { action(); }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidDataException
                || ex is InvalidOperationException || ex is NotSupportedException) { return; }
            throw new InvalidOperationException("Incomplete program gamma approval was accepted.");
        }

        public static void CommittedEvidence()
        {
            var valid = ValidEvidence();
            var approval = Approve(valid);
            Check(approval.DwmCreationFileTime == new DateTime(valid.Response.DwmStartTimeUtcTicks,
                DateTimeKind.Utc).ToFileTimeUtc(), "DateTime ticks were not converted to native FILETIME.");
            Check(approval.DwmCreationFileTime != valid.Response.DwmStartTimeUtcTicks,
                "Native FILETIME accidentally equals .NET DateTime ticks.");
            Check(approval.Gamma == 2.4 && approval.Brightness == 0.03,
                "Approval did not retain the actually applied gamma and brightness.");
            Reject(() => ProgramGammaApproval.CreateFromCommittedApply(null, valid.State,
                valid.Environment, valid.Environment, () => true));
            foreach (var mutate in new Action<Evidence>[]
            {
                evidence => evidence.Response.Success = false,
                evidence => evidence.Response.WatchdogArmed = false,
                evidence => evidence.Response.CommitAccepted = false,
                evidence => evidence.Response.RecoveryPerformed = true,
                evidence => evidence.Response.LivePatchBlocked = true,
                evidence => evidence.Response.PatchedShaderCount = 0,
                evidence => evidence.Response.PatchedConstantVectorCount = 0,
                evidence => evidence.Response.ChangedByteCount = 0,
                evidence => evidence.Response.CorrelationToken = "invalid",
                evidence => evidence.Response.PatchPlanFingerprint = new string('A', 63),
                evidence => evidence.Response.PatchActivatedUtcTicks = 0,
                evidence => evidence.State.WasApplied = false,
                evidence => evidence.State.DwmProcessId++,
                evidence => evidence.State.DwmStartTimeUtcTicks++,
                evidence => evidence.State.DwmStartTimeUtcTicks = 0,
                evidence => evidence.State.SessionId = -1,
                evidence => evidence.State.DwmCoreVersion = "different",
                evidence => evidence.State.PatchPlanFingerprint = new string('C', 64),
                evidence => evidence.State.Settings = null,
                evidence => evidence.State.Settings.ApplySdrGammaCorrection = false,
                evidence => evidence.State.Settings.Gamma = double.NaN,
                evidence => evidence.State.Settings.Contrast = 1.2
            })
            {
                var evidence = ValidEvidence();
                mutate(evidence);
                Reject(() => Approve(evidence));
            }
            Reject(() => ProgramGammaApproval.ToCreationFileTime(0));
            Reject(() => ProgramGammaApproval.ToCreationFileTime(long.MaxValue));
            Reject(() => ProgramGammaApproval.ToCreationFileTime(new DateTime(1601, 1, 1,
                0, 0, 0, DateTimeKind.Utc).Ticks));
        }

        public static void IdentityAndSettingsChanges()
        {
            var evidence = ValidEvidence();
            var approval = Approve(evidence);
            Check(approval.MatchesCurrentState(evidence.State, evidence.State.Settings.Clone(), 2),
                "Exact active evidence did not match.");
            Check(!approval.MatchesCurrentState(evidence.State, evidence.State.Settings.Clone(), 3),
                "An approval crossed the Windows session boundary.");
            Check(!approval.MatchesCurrentState(null, evidence.State.Settings.Clone(), 2),
                "Missing state still matched.");
            foreach (var mutate in new Action<PersistedDwmState>[]
            {
                state => state.WasApplied = false,
                state => state.DwmProcessId++,
                state => state.DwmStartTimeUtcTicks++,
                state => state.DwmStartTimeUtcTicks = 0,
                state => state.SessionId++,
                state => state.PatchPlanFingerprint = new string('D', 64),
                state => state.DwmCoreVersion = "different",
                state => state.Settings.Gamma = 2.2,
                state => state.Settings.Brightness = 0.04,
                state => state.Settings.ApplySdrGammaCorrection = false,
                state => state.Settings.Gamma = double.NaN,
                state => state.Settings = null
            })
            {
                var changed = ValidEvidence().State;
                mutate(changed);
                Check(!approval.MatchesCurrentState(changed, evidence.State.Settings.Clone(), 2),
                    "Changed active state still matched the approval.");
            }
            var pending = evidence.State.Settings.Clone();
            pending.Gamma = 2.2;
            Check(!approval.MatchesCurrentState(evidence.State, pending, 2),
                "Unapplied UI settings matched the active approval.");
            pending = evidence.State.Settings.Clone();
            pending.ApplySdrGammaCorrection = false;
            Check(!approval.MatchesCurrentState(evidence.State, pending, 2),
                "Disabling gamma retained the approval.");
        }

        public static void SnapshotsAndLiveness()
        {
            var evidence = ValidEvidence();
            var live = true;
            var approval = Approve(evidence, () => live);
            var expectedKey = approval.SettingsKey;
            evidence.State.Settings.Gamma = 2.2;
            evidence.State.Settings.Brightness = -0.1;
            evidence.Response.DwmProcessId = 99;
            evidence.Response.PatchPlanFingerprint = new string('C', 64);
            evidence.Environment.DwmCoreSha256 = new string('D', 64);
            var exposedEnvironment = approval.WindowsEnvironment;
            exposedEnvironment.BuildLabEx = "tampered";
            Check(approval.Gamma == 2.4 && approval.Brightness == 0.03
                && approval.DwmProcessId == 1234 && approval.SettingsKey == expectedKey
                && approval.PlanFingerprint == new string('A', 64), "Caller mutation changed the approval snapshot.");
            Check(approval.WindowsEnvironment.DwmCoreSha256 == ProgramGammaBuildSupport.SupportedDwmCoreSha256
                && approval.WindowsEnvironment.BuildLabEx != "tampered", "Windows identity was not deeply copied.");
            Check(approval.IsStillValid(), "Fresh injected liveness was rejected.");
            live = false;
            Check(!approval.IsStillValid(), "Lost liveness did not revoke approval.");
            live = true;
            Check(!approval.IsStillValid(), "Revoked approval automatically reattached.");
            var faulted = Approve(ValidEvidence(), () => { throw new IOException("read unavailable"); });
            Check(!faulted.IsStillValid() && !faulted.IsStillValid(), "A failed liveness read did not fail closed.");
        }

        public static void WindowsIdentityChanges()
        {
            foreach (var mutate in new Action<WindowsEnvironmentIdentity>[]
            {
                identity => identity.OsBuildNumber++,
                identity => identity.UpdateBuildRevision++,
                identity => identity.BuildLabEx += "-updated",
                identity => identity.DwmExecutableSha256 = new string('E', 64),
                identity => identity.DwmCoreSha256 = new string('F', 64),
                identity => identity.DwmCoreSha256 = null
            })
            {
                var evidence = ValidEvidence();
                var current = evidence.Environment.Clone();
                mutate(current);
                Reject(() => ProgramGammaApproval.CreateFromCommittedApply(evidence.Response,
                    evidence.State, evidence.Environment, current, () => true));
            }
            var unsupported = ValidEvidence();
            unsupported.Environment.DwmCoreSha256 = new string('C', 64);
            Reject(() => Approve(unsupported));
            var missing = ValidEvidence();
            Reject(() => ProgramGammaApproval.CreateFromCommittedApply(missing.Response, missing.State,
                null, missing.Environment, () => true));
        }

        public static void SeparatePrivateBuildGate()
        {
            const string privateVersion = "10.0.26100.8875 (WinBuild.160101.0800)";
            const string privateSha256 = "825726F26BD6B5A02B49F6145963B54D537D3D1239055DDA28D1FB0B3E864EF3";
            Check(ProgramGammaBuildSupport.IsSupported(privateVersion, privateSha256),
                "The reviewed private native contract was unexpectedly changed.");
            Check(ProgramGammaBuildSupport.IsSupported(privateVersion, privateSha256.ToLowerInvariant()),
                "A SHA-256 case-only change was rejected.");
            foreach (var pair in new[]
            {
                new[] { NewGlobalDwmVersion, NewGlobalDwmSha256 },
                new[] { NewGlobalDwmVersion, privateSha256 },
                new[] { privateVersion, NewGlobalDwmSha256 },
                new[] { privateVersion, new string('F', 64) },
                new[] { "10.0.26100.9999 (WinBuild.160101.0800)", privateSha256 },
                new[] { privateVersion + " ", privateSha256 },
                new[] { privateVersion, (string)null },
                new[] { (string)null, privateSha256 }
            })
            {
                Check(!ProgramGammaBuildSupport.IsSupported(pair[0], pair[1]),
                    "A changed global build, mixed version/hash, or unknown binary enabled the private hook.");
                Reject(() => ProgramGammaBuildSupport.RequireSupported(pair[0], pair[1]));
            }
            var updated = NewGlobalEvidence();
            Reject(() => Approve(updated));
        }

        public static void UnsupportedPrivateBuildPreservesGlobalCommit()
        {
            var evidence = NewGlobalEvidence();
            // Only exercise the managed post-commit decision, without running the
            // view-model constructor, touching saved settings or reading live DWM.
            var viewModel = (MainViewModel)FormatterServices.GetUninitializedObject(typeof(MainViewModel));
            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var approvalField = typeof(MainViewModel).GetField("_programGammaApproval", flags);
            var stateField = typeof(MainViewModel).GetField("_dwmState", flags);
            var establish = typeof(MainViewModel).GetMethod("EstablishProgramGammaApproval", flags);
            Check(approvalField != null && stateField != null && establish != null, "Missing post-commit test seam.");
            var oldApproval = Approve(ValidEvidence());
            approvalField.SetValue(viewModel, oldApproval);
            stateField.SetValue(viewModel, evidence.State);
            establish.Invoke(viewModel, new object[] { evidence.Response, evidence.State, evidence.Environment });
            Check(approvalField.GetValue(viewModel) == null && !oldApproval.IsStillValid(),
                "An unsupported private build created or retained a native hook approval.");
            Check(ReferenceEquals(stateField.GetValue(viewModel), evidence.State)
                && evidence.State.WasApplied && evidence.Response.Success && evidence.Response.CommitAccepted,
                "The unsupported optional private hook discarded a successful global commit.");
            var rejected = false;
            try { viewModel.GetProgramGammaApproval(); }
            catch (NotSupportedException ex) { rejected = ex.Message == ProgramGammaBuildSupport.UnsupportedMessage; }
            Check(rejected, "The program list did not explain separate private-hook incompatibility.");

            // The same actionable explanation is available before a global Apply;
            // there must not be an endless request to repeat the five-second test.
            stateField.SetValue(viewModel, null);
            typeof(MainViewModel).GetField("_preflight", flags).SetValue(viewModel, evidence.Response);
            rejected = false;
            try { viewModel.GetProgramGammaApproval(); }
            catch (NotSupportedException ex) { rejected = ex.Message == ProgramGammaBuildSupport.UnsupportedMessage; }
            Check(rejected, "A known unsupported build misleadingly requested global reapproval.");
        }

        private static Evidence NewGlobalEvidence()
        {
            var evidence = ValidEvidence();
            evidence.Response.DwmCoreVersion = NewGlobalDwmVersion;
            evidence.State.DwmCoreVersion = NewGlobalDwmVersion;
            evidence.Environment.DwmCoreSha256 = NewGlobalDwmSha256;
            evidence.Environment.UpdateBuildRevision = 9278;
            return evidence;
        }
    }
}
