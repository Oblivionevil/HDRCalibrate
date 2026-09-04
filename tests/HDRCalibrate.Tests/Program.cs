using HDRCalibrate.Core;
using HDRCalibrate.Dwm;
using HDRCalibrate.Services;
using HDRCalibrate.ViewModels;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace HDRCalibrate.Tests
{
    internal static class Program
    {
        private static readonly List<string> Passed = new List<string>();

        private static int Main()
        {
            try
            {
                Run("PQ round-trip", TestPqRoundTrip);
                Run("Neutral curve is identity", TestNeutralCurve);
                Run("SDR target gamma 2.0, 2.2 and 2.4", TestGammaTargets);
                Run("Gamma preset commands", TestGammaPresetCommands);
                Run("Generated LUT is monotonic", TestMonotonicCurve);
                Run("Neutral color matrix is identity", TestNeutralMatrix);
                Run("Fallback ICC structure", TestFallbackIcc);
                Run("Windows accepts generated ICC", TestWindowsIccValidation);
                Run("Active display detection", TestDisplayDetection);
                Run("Current display profile generation", TestCurrentDisplayProfileGeneration);
                Run("SDR-only adjustment math", TestSdrAdjustmentMath);
                Run("Installed dwmcore six-export manifest", TestDwmCoreCompatibility);
                Run("DWM ABI-preserving constant patch dry-run", TestDwmShaderRewrite);
                Run("DWM constant patch boundary matrix", TestDwmShaderRewriteBoundaryMatrix);
                Run("Unexpected DWM shader bytes are rejected", TestDwmShaderAbiMismatchRejected);
                Run("Unsafe full-shader controls are rejected", TestUnsupportedDwmSettings);
                Run("DWM read-only preflight", TestDwmPreflight);
                Run("DWM patch-plan fingerprint is deterministic and settings-bound", TestDwmPatchPlanFingerprint);
                Run("DWM file and mapped-image patch plans agree", TestMappedDwmPatchPlanFingerprint);
                Run("Timed DWM protocol validation", TestTimedDwmTestSequence);
                Run("Timed DWM recovery result validation", TestTimedDwmTestTimingCompensation);
                Run("Supervised apply accepts only an exact committed handshake", TestSupervisedApplyProtocol);
                Run("Supervised apply rejects tampered or incomplete evidence", TestSupervisedApplyProtocolRejections);
                Run("DWM command contract defaults to Unknown", TestTimedDwmTestFailureReset);
                Run("DWM state persistence", TestDwmStatePersistence);
                Run("Startup activation approval is exact and fail-closed", TestStartupActivationPolicy);
                Run("Startup activation policy persistence is atomic", TestStartupActivationPolicyPersistence);
                Run("Startup activation disable failures remain visible", TestStartupActivationDisableFailure);
                Run("Windows environment approval compares every update identity", TestWindowsEnvironmentIdentity);
                Run("Windows environment guard fails closed before side effects", TestWindowsEnvironmentGuard);
                Run("Supervised apply refuses missing Windows test evidence before starting", TestSupervisedApplyRequiresWindowsEnvironment);
                Run("DWM IPC round-trips the exact expected Windows environment", TestWindowsEnvironmentRequestRoundTrip);
                Run("Startup approval file revocation blocks an in-flight activation", TestStartupApprovalFileProof);
                Run("Startup proof capture requires the original enabled approval", TestStartupProofCaptureRequiresEnabledPolicy);
                Run("Startup activation rejects every Windows update component", TestStartupWindowsUpdateMatrix);
                Run("Startup activation rejects legacy and incomplete Windows identities", TestStartupInvalidWindowsIdentity);
                Run("Windows identity capture failures remain fail-closed", TestStartupWindowsIdentityCaptureFailure);
                Run("Timed test approval is bound to the unchanged Windows environment", TestTimedTestWindowsEnvironmentBinding);
                Run("Windows update revocation survives restart and build rollback", TestStartupWindowsUpdateRevocationPersistence);
                Run("Disabled startup approvals are revoked after Windows updates", TestDisabledStartupWindowsUpdateRevocation);
                Run("Windows update revocation failures retain a safe startup fallback", TestStartupWindowsUpdateRevocationFailures);
                Run("A new Windows environment needs a new test and explicit startup enable", TestStartupWindowsUpdateReapproval);
                Run("WPF view model preflight initialization", TestViewModelInitialization);
                Run("Stale legacy profile rollback is idempotent", TestStaleLegacyProfileRollback);
                Run("Autostart command is exact and quoted", TestAutostartCommand);
                Run("Autostart store toggles per-user Run value", TestAutostartToggle);
                Run("Autostart UI reports registry failures", TestAutostartUiFailure);
                Run("Tray and installer arguments are parsed exactly", TestStartupArguments);
                Run("Single-instance activation and exit signals", TestSingleInstanceSignals);
                Run("Exit requests wait for critical operations", TestExitRequestGate);

                Console.WriteLine($"PASS: {Passed.Count} Tests");
                foreach (var name in Passed) Console.WriteLine("  ✓ " + name);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL: " + ex);
                return 1;
            }
        }

        private static void Run(string name, Action test)
        {
            test();
            Passed.Add(name);
        }

        private static void TestAutostartCommand()
        {
            var path = @"C:\Program Files\HDR Calibrate\HDRCalibrate.exe";
            Assert(AutostartService.BuildCommandLine(path) ==
                @"""C:\Program Files\HDR Calibrate\HDRCalibrate.exe"" --tray --startup",
                "Autostart command does not quote the exact executable path and append --tray --startup.");

            var samplePath = @"C:\a.exe";
            var commandOverhead = AutostartService.BuildCommandLine(samplePath).Length
                - Path.GetFullPath(samplePath).Length;
            var maximumFileStemLength = 260 - commandOverhead - @"C:\".Length - ".exe".Length;
            var maximumPath = @"C:\" + new string('a', maximumFileStemLength) + ".exe";
            var excessivePath = @"C:\" + new string('a', maximumFileStemLength + 1) + ".exe";

            foreach (var invalidPath in new[]
            {
                @"relative\HDRCalibrate.exe",
                "C:\\bad\"path.exe",
                excessivePath
            })
            {
                var rejected = false;
                try
                {
                    AutostartService.BuildCommandLine(invalidPath);
                }
                catch (ArgumentException)
                {
                    rejected = true;
                }
                Assert(rejected, "An unsafe autostart executable path was accepted: " + invalidPath);
            }
            Assert(AutostartService.BuildCommandLine(maximumPath).Length == 260,
                "A valid 260-character autostart command was rejected.");
        }

        private static void TestAutostartToggle()
        {
            var store = new MemoryRunValueStore();
            var service = new AutostartService(store, @"C:\Apps\HDRCalibrate.exe");
            Assert(!service.IsEnabled(), "Autostart was enabled without a Run value.");

            service.SetEnabled(true);
            Assert(store.ValueName == AutostartService.ValueName, "Autostart used an unexpected Run value name.");
            Assert(store.Value == @"""C:\Apps\HDRCalibrate.exe"" --tray --startup", "Autostart wrote an unexpected command.");
            Assert(service.IsEnabled(), "Autostart did not recognize its exact Run command.");
            Assert(!service.NeedsMigration(), "The current startup command was marked as legacy.");

            store.Value = @"""C:\Apps\HDRCalibrate.exe"" --tray";
            Assert(service.IsEnabled(), "The legacy tray-only command was not recognized for safe migration.");
            Assert(service.NeedsMigration(), "The legacy tray-only command was not marked for migration.");
            service.SetEnabled(true);
            Assert(store.Value == @"""C:\Apps\HDRCalibrate.exe"" --tray --startup",
                "Re-enabling autostart did not migrate the legacy command.");

            store.Value = @"C:\Apps\HDRCalibrate.exe --tray --startup";
            Assert(!service.IsEnabled(), "Autostart accepted an unquoted executable command.");
            store.Value = @"""c:\Apps\HDRCalibrate.exe"" --tray --startup";
            Assert(!service.IsEnabled(), "Autostart accepted a command other than its exact normalized value.");
            service.SetEnabled(false);
            Assert(store.Value == null, "Disabling autostart did not remove the Run value.");
        }

        private static void TestAutostartUiFailure()
        {
            var service = new AutostartService(new ThrowingRunValueStore(), @"C:\Apps\HDRCalibrate.exe");
            var viewModel = new MainViewModel(service, new MemoryStartupActivationPolicyStore());
            Assert(!viewModel.IsAutostartEnabled, "An unreadable Run value was displayed as enabled.");
            Assert(viewModel.AutostartStatus.StartsWith("Status nicht verfügbar", StringComparison.Ordinal),
                "The autostart read error is not visible in the UI state.");

            viewModel.ToggleAutostartCommand.Execute(null);
            Assert(viewModel.IsError, "The autostart write error did not set the visible error state.");
            Assert(viewModel.StatusMessage.StartsWith("Autostart konnte nicht geändert werden", StringComparison.Ordinal),
                "The autostart write error is not understandable in the UI state.");
        }

        private static void TestStartupArguments()
        {
            Assert(StartupOptions.ShouldStartInTray(new[] { "--tray" }), "--tray was not recognized.");
            Assert(StartupOptions.ShouldStartInTray(new[] { "--TRAY" }), "--tray parsing is unexpectedly case-sensitive.");
            Assert(!StartupOptions.ShouldStartInTray(new[] { "--tray=true" }), "A non-exact tray argument was accepted.");
            Assert(!StartupOptions.ShouldStartInTray(null), "Null arguments enabled tray startup.");
            Assert(StartupOptions.ShouldExitExisting(new[] { "--exit-existing" }), "--exit-existing was not recognized.");
            Assert(!StartupOptions.ShouldExitExisting(new[] { "--exit" }), "A partial exit argument was accepted.");
            Assert(StartupOptions.ShouldActivateAtStartup(new[] { "--startup" }), "--startup was not recognized.");
            Assert(StartupOptions.ShouldActivateAtStartup(new[] { "--STARTUP" }), "--startup parsing is unexpectedly case-sensitive.");
            Assert(!StartupOptions.ShouldActivateAtStartup(new[] { "--startup=true" }), "A non-exact startup activation argument was accepted.");
            Assert(!StartupOptions.ShouldStartInTray(new[] { "--startup" }), "--startup unexpectedly implied tray mode.");
        }

        private static void TestSingleInstanceSignals()
        {
            var identity = "HDRCalibrate.Tests." + Guid.NewGuid().ToString("N");
            using (var activationReceived = new ManualResetEvent(false))
            using (var exitReceived = new ManualResetEvent(false))
            using (var primary = new SingleInstanceCoordinator(identity))
            {
                Assert(primary.TryAcquire(), "The first coordinator did not become primary.");
                primary.StartListening(
                    () => activationReceived.Set(),
                    () => exitReceived.Set());

                Exception workerError = null;
                var worker = new Thread(() =>
                {
                    try
                    {
                        using (var secondary = new SingleInstanceCoordinator(identity))
                        {
                            Assert(!secondary.TryAcquire(), "A second coordinator became primary.");
                            secondary.SignalActivation();
                            secondary.SignalExit();
                            Assert(!secondary.WaitForPrimaryExit(TimeSpan.FromMilliseconds(25)),
                                "The secondary acquired the mutex before primary shutdown.");
                        }
                    }
                    catch (Exception ex)
                    {
                        workerError = ex;
                    }
                });
                worker.Start();
                Assert(worker.Join(2000), "The secondary coordinator did not complete.");
                if (workerError != null) throw workerError;
                Assert(activationReceived.WaitOne(2000), "The activation signal was not received.");
                Assert(exitReceived.WaitOne(2000), "The exit signal was not received.");
            }
        }

        private static void TestExitRequestGate()
        {
            var gate = new ExitRequestGate();
            Assert(gate.Request(false), "An idle application rejected an exit request.");
            Assert(!gate.IsPending, "An idle exit request was marked pending.");
            Assert(!gate.Request(true), "A busy application accepted an unsafe exit request.");
            Assert(gate.IsPending, "The deferred exit request was not remembered.");
            Assert(!gate.CanComplete(true), "A deferred exit became ready while the operation was still busy.");
            Assert(gate.CanComplete(false), "A deferred exit did not become ready after the operation completed.");
            gate.Complete();
            Assert(!gate.IsPending, "The completed exit request remained pending.");
        }

        private static void TestPqRoundTrip()
        {
            foreach (var nits in new[] { 0.0, 0.1, 80.0, 203.0, 1000.0, 4000.0, 10000.0 })
            {
                var decoded = HdrToneCurve.PqDecodeNits(HdrToneCurve.PqEncodeNits(nits));
                AssertClose(nits, decoded, Math.Max(0.0001, nits * 0.000001), "PQ round-trip");
            }
        }

        private static void TestNeutralCurve()
        {
            var curve = HdrToneCurve.BuildLut(new CalibrationSettings(), 1024, 203.0, 1000.0);
            for (var index = 0; index < curve.Length; index++)
            {
                AssertClose((double)index / (curve.Length - 1), curve[index], 1e-10, "Neutral curve");
            }
        }

        private static void TestGammaTargets()
        {
            const double sdrWhiteNits = 203.0;
            const double sourceCode = 0.5;
            var sourceLinear = HdrToneCurve.SrgbToLinear(sourceCode);
            var inputPq = HdrToneCurve.PqEncodeNits(sourceLinear * sdrWhiteNits);
            var results = new List<double>();

            foreach (var gamma in new[] { 2.0, 2.2, 2.4 })
            {
                var settings = new CalibrationSettings
                {
                    Gamma = gamma,
                    ApplySdrGammaCorrection = true,
                    ProtectHighlights = true
                };
                var actual = HdrToneCurve.Evaluate(settings, inputPq, sdrWhiteNits, 1000.0);
                var expected = HdrToneCurve.PqEncodeNits(Math.Pow(sourceCode, gamma) * sdrWhiteNits);
                AssertClose(expected, actual, 1e-10, "SDR target gamma " + gamma);
                results.Add(actual);
            }

            Assert(results[0] > results[1] && results[1] > results[2], "Higher target gamma should darken SDR midtones.");

            var hdrInput = HdrToneCurve.PqEncodeNits(600.0);
            var hdrOutput = HdrToneCurve.Evaluate(
                new CalibrationSettings { Gamma = 2.4, ApplySdrGammaCorrection = true },
                hdrInput,
                sdrWhiteNits,
                1000.0);
            AssertClose(hdrInput, hdrOutput, 1e-10, "Native HDR signal above SDR white");
        }

        private static void TestMonotonicCurve()
        {
            var settings = new CalibrationSettings
            {
                Gamma = 2.6,
                ApplySdrGammaCorrection = true,
                Brightness = 0.2,
                Contrast = 1.5,
                ProtectHighlights = true
            };
            var curve = HdrToneCurve.BuildLut(settings, 4096, 203.0, 1000.0);
            Assert(curve[0] >= 0.0 && curve[curve.Length - 1] == 1.0, "LUT endpoints invalid.");
            for (var index = 1; index < curve.Length; index++)
                Assert(curve[index] >= curve[index - 1], "LUT is not monotonic.");
        }

        private static void TestGammaPresetCommands()
        {
            var viewModel = CreateIsolatedViewModel();
            viewModel.NeutralCommand.Execute(null);
            Assert(!viewModel.ApplySdrGammaCorrection, "Neutral must disable SDR gamma correction.");
            viewModel.SetGammaPresetCommand.Execute("2.4");
            Assert(viewModel.ApplySdrGammaCorrection, "A gamma preset must enable SDR gamma correction.");
            AssertClose(2.4, viewModel.Gamma, 1e-12, "Gamma preset value");
        }

        private static void TestNeutralMatrix()
        {
            var matrix = ColorAdjustmentMatrix.CreateXyzAdjustment(new CalibrationSettings());
            for (var row = 0; row < 3; row++)
            {
                for (var column = 0; column < 3; column++)
                {
                    AssertClose(row == column ? 1.0 : 0.0, matrix[row, column], 1e-9, "Neutral matrix");
                }
            }
        }

        private static void TestFallbackIcc()
        {
            var bytes = CreateFallback();
            var info = IccProfileInspector.Inspect(bytes);
            Assert(info.Size == bytes.Length, "ICC size mismatch.");
            Assert(info.HasMhc2, "MHC2 tag missing.");
            Assert(info.LutEntryCount == 1024, "Unexpected LUT size.");
            Assert(info.Tags.Contains("desc") && info.Tags.Contains("lumi") && info.Tags.Contains("rXYZ"), "Required ICC tags missing.");
            Assert(info.Description.Contains("Testprofil"), "ICC description missing.");
        }

        private static void TestWindowsIccValidation()
        {
            var path = Path.Combine(Path.GetTempPath(), "HDRCalibrate_Test_" + Guid.NewGuid().ToString("N") + ".icm");
            try
            {
                File.WriteAllBytes(path, CreateFallback());
                Assert(DisplayService.ValidateProfileFile(path, out var error), "Windows ICC validation failed: " + error);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        private static void TestDisplayDetection()
        {
            var displays = new DisplayService().GetActiveDisplays();
            Assert(displays.Count > 0, "No active display detected.");
            foreach (var display in displays)
            {
                Assert(!string.IsNullOrWhiteSpace(display.GdiDeviceName), "Display has no GDI name.");
                Console.WriteLine($"DISPLAY: {display.FriendlyName}; HDR active={display.HdrActive}; MHC2={display.Mhc2Supported}; LUT max={display.Mhc2LutEntries}; {display.BitDepthLabel}; SDR white={display.SdrWhiteLabel}; peak={display.ColorMetadata.MaxLuminanceNits:0.###} nit; full-frame={display.ColorMetadata.MaxFullFrameLuminanceNits:0.###} nit; profile={display.ProfileLabel}");
            }
        }

        private static void TestCurrentDisplayProfileGeneration()
        {
            var display = new DisplayService().GetActiveDisplays().First();
            var settings = new CalibrationSettings { Gamma = 2.2, ApplySdrGammaCorrection = true };
            var adjusted = File.Exists(display.ActiveProfilePath)
                ? IccProfileFactory.CreateAdjustedProfile(
                    File.ReadAllBytes(display.ActiveProfilePath),
                    "HDR Calibrate Clone Test",
                    settings,
                    1024,
                    display.SdrWhiteNits,
                    display.ColorMetadata)
                : IccProfileFactory.CreateFallbackProfile(
                    "HDR Calibrate Display Test",
                    display.ColorMetadata,
                    settings,
                    1024,
                    display.SdrWhiteNits);
            var info = IccProfileInspector.Inspect(adjusted);
            Assert(info.HasMhc2 && info.LutEntryCount == 1024, "Cloned profile has no composed MHC2 LUT.");

            var path = Path.Combine(Path.GetTempPath(), "HDRCalibrate_CloneTest_" + Guid.NewGuid().ToString("N") + ".icm");
            try
            {
                File.WriteAllBytes(path, adjusted);
                Assert(DisplayService.ValidateProfileFile(path, out var error), "Windows rejected cloned ICC: " + error);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        private static void TestSdrAdjustmentMath()
        {
            const double encoded = 0.5;
            var neutral = new CalibrationSettings { ApplySdrGammaCorrection = false };
            AssertClose(
                SdrAdjustmentMath.SrgbToLinear(encoded),
                SdrAdjustmentMath.EvaluateGray(neutral, encoded),
                1e-12,
                "Neutral SDR sRGB decode");

            foreach (var gamma in new[] { 2.0, 2.2, 2.4, 2.6 })
            {
                var settings = new CalibrationSettings
                {
                    Gamma = gamma,
                    ApplySdrGammaCorrection = true
                };
                AssertClose(
                    Math.Pow(encoded, gamma),
                    SdrAdjustmentMath.EvaluateGray(settings, encoded),
                    1e-12,
                    "DWM SDR gamma " + gamma);
            }

            var adjusted = SdrAdjustmentMath.EvaluateRgb(
                new CalibrationSettings
                {
                    ApplySdrGammaCorrection = true,
                    Gamma = 2.2,
                    Brightness = 0.1,
                    Saturation = 0.0,
                    RedGain = 1.2,
                    GreenGain = 0.8,
                    BlueGain = 1.0
                },
                0.8,
                0.2,
                0.4);
            AssertClose(Math.Pow(0.8, 2.2) * 1.1, adjusted[0], 1e-12, "Safe red preview");
            AssertClose(Math.Pow(0.2, 2.2) * 1.1, adjusted[1], 1e-12, "Safe green preview");
            AssertClose(Math.Pow(0.4, 2.2) * 1.1, adjusted[2], 1e-12, "Safe blue preview");
        }

        private static void TestDwmCoreCompatibility()
        {
            var info = DwmCoreInspector.InspectSystem();
            Assert(info.IsCompatible, "Installed dwmcore.dll is not compatible: " + info.Reason);
            Assert(info.TargetShaderCount == 4, "Expected exactly four SDR target shaders.");
            Assert(info.ValidatedSdrShaderCount == 6, "Expected exactly six profiled SDR exports.");
            Assert(info.ValidatedParentLibraryCount == 3, "Expected exactly three SDR profile libraries.");
            Assert(string.Equals(info.DwmCoreSha256, DwmCoreInspector.SupportedDwmCoreSha256,
                StringComparison.OrdinalIgnoreCase), "dwmcore.dll SHA-256 is not pinned.");
            Assert(!string.IsNullOrWhiteSpace(info.DwmCoreVersion), "dwmcore.dll has no version.");
            Console.WriteLine("DWMCORE: " + info.DwmCoreVersion + "; " + info.Reason);
        }

        private static void TestDwmShaderRewrite()
        {
            var path = DwmCoreInspector.GetSystemDwmCorePath();
            var original = File.ReadAllBytes(path);
            var settings = new CalibrationSettings
            {
                Gamma = 2.4,
                ApplySdrGammaCorrection = true,
                Brightness = -0.05
            };
            var modified = DwmShaderRewriter.Rewrite(original, settings, out var report);

            Assert(report.PatchedShaderCount == 4, "Dry-run did not patch exactly four SDR shaders.");
            Assert(report.PatchedConstantVectorCount == 16, "Dry-run did not patch exactly 16 SHDR constant vectors.");
            Assert(report.ParentLibraryCount == 2, "Unexpected number of parent shader libraries.");
            Assert(report.ModifiedPageCount > 0, "Dry-run has no modified pages.");
            Assert(report.ChangedByteCount > 0 && report.ChangedByteCount < 512,
                "Constant patch changed an implausible number of bytes.");
            Assert(report.ModifiedRanges.Sum(range => range.Length) == report.ChangedByteCount,
                "Byte-range report does not match its changed-byte count.");
            Assert(report.ModifiedRanges.All(range => range.Offset >= 0 && range.Length > 0
                && range.Offset <= original.Length - range.Length), "A byte-range lies outside dwmcore.dll.");
            for (var rangeIndex = 1; rangeIndex < report.ModifiedRanges.Count; rangeIndex++)
                Assert(report.ModifiedRanges[rangeIndex - 1].Offset + report.ModifiedRanges[rangeIndex - 1].Length
                    < report.ModifiedRanges[rangeIndex].Offset, "Byte-ranges overlap or include unchanged bytes.");
            Assert(modified.Length == original.Length, "Mapped image size changed during rewrite.");
            Assert(!ReferenceEquals(modified, original), "Rewrite modified the source buffer in-place.");
            Assert(DwmShaderRewriter.FindModifiedPages(original, modified).SequenceEqual(report.ModifiedPageOffsets), "Modified-page report differs from byte comparison.");

            var originalBlobs = DxbcContainer.FindAll(original);
            var targets = new[]
            {
                "96E6D1589255ECCD1DD7D4DBEC54D285",
                "2126B037C1A2FBDDE355B6E6DD9CAF3C",
                "2C8926FFE229F05D967C72668DC3ADDB",
                "F693BFBBAF24B3D9366354BE8898A7F5"
            };
            foreach (var checksum in targets)
            {
                var originalBlob = originalBlobs.Single(blob => string.Equals(
                    DxbcContainer.GetStoredChecksumHex(original, blob), checksum, StringComparison.OrdinalIgnoreCase));
                DxbcBlob patchedBlob;
                Assert(DxbcContainer.TryParse(modified, originalBlob.Offset, out patchedBlob),
                    "Patched target cannot be parsed at its original address.");
                Assert(originalBlob.Size == patchedBlob.Size, "Patched target changed its fixed LIBF slot size.");
                Assert(originalBlob.Chunks.Select(chunk => chunk.FourCc)
                    .SequenceEqual(patchedBlob.Chunks.Select(chunk => chunk.FourCc)),
                    "Patched target changed its chunk order.");
                for (var index = 0; index < originalBlob.Chunks.Count; index++)
                {
                    var before = originalBlob.Chunks[index];
                    var after = patchedBlob.Chunks[index];
                    Assert(before.HeaderOffset - originalBlob.Offset == after.HeaderOffset - patchedBlob.Offset,
                        "Patched target moved a chunk header.");
                    Assert(before.DataOffset - originalBlob.Offset == after.DataOffset - patchedBlob.Offset,
                        "Patched target moved a chunk payload.");
                    Assert(before.DataSize == after.DataSize, "Patched target changed a fixed chunk size.");
                }
                foreach (var fourCc in new[] { "Aon9", "STAT", "RDEF", "LFS0" })
                    AssertChunkBytesEqual(original, originalBlob.GetChunk(fourCc), modified, patchedBlob.GetChunk(fourCc),
                        "Constant patch changed the " + fourCc + " chunk.");
                Assert(BitConverter.ToUInt32(original, originalBlob.GetChunk("Aon9").DataOffset + 36)
                    == BitConverter.ToUInt32(modified, patchedBlob.GetChunk("Aon9").DataOffset + 36),
                    "Patched target changed the 9.1/9.3 Aon9 profile token.");
                Assert(DxbcContainer.VerifyChecksum(modified, patchedBlob), "Patched target checksum is invalid.");
                Assert(!string.Equals(DxbcContainer.GetStoredChecksumHex(modified, patchedBlob), checksum,
                    StringComparison.OrdinalIgnoreCase), "Patched target still has the original checksum.");
                D3DCompiler.ValidateDxbc(DxbcContainer.Slice(modified, patchedBlob), "fixed-layout patched target");
            }

            foreach (var checksum in new[]
            {
                "4CDE30EDFF9BEE225DA9465FDF59CD84",
                "7042A5DE5C816B2D89D14AD3CCD76569"
            })
            {
                var native = originalBlobs.Single(blob => string.Equals(
                    DxbcContainer.GetStoredChecksumHex(original, blob), checksum, StringComparison.OrdinalIgnoreCase));
                for (var index = 0; index < native.Size; index++)
                    Assert(original[native.Offset + index] == modified[native.Offset + index],
                        "The unpatched FL10 SDR export changed.");
            }
        }

        private static void TestDwmShaderAbiMismatchRejected()
        {
            var original = File.ReadAllBytes(DwmCoreInspector.GetSystemDwmCorePath());
            var corrupted = (byte[])original.Clone();
            var target = DxbcContainer.FindAll(original).Single(blob => string.Equals(
                DxbcContainer.GetStoredChecksumHex(original, blob),
                "2126B037C1A2FBDDE355B6E6DD9CAF3C",
                StringComparison.OrdinalIgnoreCase));
            corrupted[target.GetChunk("SHDR").DataOffset + 12] ^= 0x01;

            var rejected = false;
            try
            {
                DwmShaderRewriter.Rewrite(corrupted, new CalibrationSettings
                {
                    Gamma = 2.2,
                    ApplySdrGammaCorrection = true
                }, out _);
            }
            catch (InvalidDataException)
            {
                rejected = true;
            }
            Assert(rejected, "A non-whitelisted target byte sequence was accepted.");
        }

        private static void TestDwmShaderRewriteBoundaryMatrix()
        {
            var original = File.ReadAllBytes(DwmCoreInspector.GetSystemDwmCorePath());
            foreach (var gamma in new[] { 1.8, 2.0, 2.2, 2.4, 2.6 })
            {
                foreach (var brightness in new[] { -0.2, 0.0, 0.2 })
                {
                    var modified = DwmShaderRewriter.Rewrite(original, new CalibrationSettings
                    {
                        Gamma = gamma,
                        Brightness = brightness,
                        ApplySdrGammaCorrection = true
                    }, out var report);
                    Assert(modified.Length == original.Length, "Boundary rewrite changed module size.");
                    Assert(report.PatchedShaderCount == 4, "Boundary rewrite target count changed.");
                    Assert(report.PatchedConstantVectorCount == 16, "Boundary rewrite constant count changed.");
                    Assert(report.ParentLibraryCount == 2, "Boundary rewrite parent count changed.");
                    Assert(report.ModifiedPageCount > 0, "Boundary rewrite produced no changed pages.");
                }
            }
        }

        private static void TestUnsupportedDwmSettings()
        {
            var rejected = false;
            try
            {
                DwmShaderRewriter.ValidateSupportedSettings(new CalibrationSettings
                {
                    Gamma = 2.2,
                    ApplySdrGammaCorrection = true,
                    Saturation = 1.1
                });
            }
            catch (NotSupportedException)
            {
                rejected = true;
            }
            Assert(rejected, "A full-shader color adjustment reached the constant-only patch path.");
        }

        private static void TestDwmPreflight()
        {
            var response = new DwmPatchEngine().Preflight(new CalibrationSettings
            {
                Gamma = 2.2,
                ApplySdrGammaCorrection = true
            });
            Assert(response.Success, "Read-only DWM preflight failed: " + response.Message);
            Assert(response.PatchedShaderCount == 4, "Preflight did not validate four patch targets.");
            Assert(response.PatchedConstantVectorCount == 16, "Preflight did not validate 16 constant vectors.");
            Assert(response.ModifiedPageCount > 0, "Preflight found no changed pages.");
            Assert(IsUppercaseSha256(response.PatchPlanFingerprint),
                "Preflight did not return a valid patch-plan fingerprint.");

            var neutral = new DwmPatchEngine().Preflight(new CalibrationSettings());
            Assert(!neutral.Success,
                "Neutral settings unexpectedly passed the persistent DWM patch preflight.");
            Assert(string.IsNullOrEmpty(neutral.PatchPlanFingerprint),
                "Neutral settings unexpectedly produced a persistent patch plan.");
        }

        private static void TestDwmPatchPlanFingerprint()
        {
            var engine = new DwmPatchEngine();
            var gamma22 = new CalibrationSettings
            {
                Gamma = 2.2,
                Brightness = 0.05,
                ApplySdrGammaCorrection = true
            };
            var first = engine.Preflight(gamma22);
            var second = engine.Preflight(gamma22.Clone());
            var different = engine.Preflight(new CalibrationSettings
            {
                Gamma = 2.4,
                Brightness = 0.05,
                ApplySdrGammaCorrection = true
            });

            Assert(first.Success && second.Success && different.Success,
                "Patch-plan fingerprint dry-runs did not all pass preflight.");
            Assert(IsUppercaseSha256(first.PatchPlanFingerprint),
                "Patch-plan fingerprint is not an uppercase SHA-256 value.");
            Assert(first.PatchPlanFingerprint == second.PatchPlanFingerprint,
                "The same settings produced different patch-plan fingerprints.");
            Assert(first.PatchPlanFingerprint != different.PatchPlanFingerprint,
                "Different gamma settings produced the same patch-plan fingerprint.");
        }

        private static void TestMappedDwmPatchPlanFingerprint()
        {
            var settings = new CalibrationSettings
            {
                Gamma = 2.2,
                Brightness = 0.05,
                ApplySdrGammaCorrection = true
            };
            var diskImage = File.ReadAllBytes(DwmCoreInspector.GetSystemDwmCorePath());
            var modifiedDiskImage = DwmShaderRewriter.Rewrite(
                diskImage,
                settings,
                out var diskReport);
            var mappedImage = MapPortableExecutableImage(diskImage);
            var modifiedMappedImage = DwmShaderRewriter.Rewrite(
                mappedImage,
                settings,
                out var mappedReport);

            Assert(diskReport.PatchedShaderCount == mappedReport.PatchedShaderCount
                && diskReport.PatchedConstantVectorCount == mappedReport.PatchedConstantVectorCount
                && diskReport.ParentLibraryCount == mappedReport.ParentLibraryCount
                && diskReport.ChangedByteCount == mappedReport.ChangedByteCount,
                "The mapped DWM image produced a different patch plan than the file image.");
            Assert(diskReport.ModifiedRanges.Count == mappedReport.ModifiedRanges.Count,
                "The mapped DWM image produced a different number of write ranges.");
            for (var index = 0; index < diskReport.ModifiedRanges.Count; index++)
            {
                var diskRange = diskReport.ModifiedRanges[index];
                var mappedRange = mappedReport.ModifiedRanges[index];
                Assert(diskRange.Offset == mappedRange.Offset
                    && diskRange.Length == mappedRange.Length,
                    "A mapped DWM write range does not use the file-image offset and length.");
            }

            var diskFingerprint = ComputePatchPlanFingerprint(modifiedDiskImage, diskReport.ModifiedRanges);
            var mappedFingerprint = ComputePatchPlanFingerprint(modifiedMappedImage, mappedReport.ModifiedRanges);
            Assert(diskFingerprint == mappedFingerprint,
                "The mapped DWM image patch fingerprint differs from the preflight file-image fingerprint.");
            var preflight = new DwmPatchEngine().Preflight(settings);
            Assert(preflight.Success && preflight.PatchPlanFingerprint == diskFingerprint,
                "DWM preflight did not publish the verified file-image patch fingerprint.");
        }

        private static void TestTimedDwmTestSequence()
        {
            TimedDwmTestRunner.ValidateSettings(
                new CalibrationSettings { Gamma = 2.4, ApplySdrGammaCorrection = true });
            Assert(TimedDwmTestRunner.DurationMilliseconds == 5000, "Timed test is not fixed at five seconds.");
            Assert(TimedDwmTestRunner.SetupDeadlineMilliseconds > TimedDwmTestRunner.DurationMilliseconds,
                "Setup deadline does not protect a hung patch worker.");
            Assert(TimedDwmTestRunner.IsValidCorrelationToken(Guid.NewGuid().ToString("N")),
                "A generated correlation token was rejected.");
            Assert(!TimedDwmTestRunner.IsValidCorrelationToken("not-a-token"),
                "An invalid correlation token was accepted.");
            var now = DateTime.UtcNow;
            Assert(TimedDwmTestRunner.GetRemainingDurationMilliseconds(now.AddMilliseconds(-1250).Ticks, now) == 3750,
                "Timed test did not compensate elapsed activation time.");
        }

        private static void TestTimedDwmTestFailureReset()
        {
            Assert((int)DwmPatchCommand.Unknown == 0, "Unknown is not the default DWM command.");
            Assert((DwmPatchCommand)new DwmPatchRequest().Command == DwmPatchCommand.Unknown,
                "A request without an explicit command does not fail closed.");
            Assert((int)DwmPatchCommand.Apply != 0 && (int)DwmPatchCommand.TimedTest != 0,
                "A mutating DWM command still uses the default enum value.");
            Assert((int)DwmPatchCommand.SupervisedApply != 0
                && (int)DwmPatchCommand.SupervisedApplyWorker != 0
                && (int)DwmPatchCommand.SupervisedApplyWatchdog != 0,
                "A supervised persistent command uses the default enum value.");
            Assert(!string.IsNullOrWhiteSpace(DwmPatchSafety.PatchImplementationId),
                "The persistent patch implementation has no versioned identity.");
        }

        private static void TestTimedDwmTestTimingCompensation()
        {
            var applied = new DwmPatchResponse
            {
                Success = true,
                DwmProcessId = 100,
                DwmStartTimeUtcTicks = 1000,
                PatchActivatedUtcTicks = DateTime.UtcNow.AddSeconds(-5).Ticks,
                PatchedShaderCount = 4,
                PatchedConstantVectorCount = 16,
                ModifiedPageCount = 6,
                DwmCoreVersion = DwmCoreInspector.SupportedDwmCoreVersion,
                PatchPlanFingerprint = new string('A', 64)
            };
            var reset = new DwmPatchResponse
            {
                Success = true,
                DwmProcessId = 101,
                DwmStartTimeUtcTicks = 2000,
                WatchdogResetUtcTicks = DateTime.UtcNow.Ticks
            };
            var combined = TimedDwmTestRunner.Combine(applied, reset, 5000);
            Assert(combined.Success && combined.WatchdogArmed, "A valid watchdog result was not accepted.");
            Assert(combined.TestedDwmProcessId == 100 && combined.DwmProcessId == 101,
                "The tested and reset DWM identities were not preserved.");
            Assert(combined.PatchPlanFingerprint == applied.PatchPlanFingerprint,
                "The timed-test result lost its exact patch-plan fingerprint.");

            var rejectedSameIdentity = false;
            try
            {
                TimedDwmTestRunner.Combine(applied, new DwmPatchResponse
                {
                    Success = true,
                    DwmProcessId = 100,
                    DwmStartTimeUtcTicks = 1000
                }, 5000);
            }
            catch (InvalidOperationException)
            {
                rejectedSameIdentity = true;
            }
            Assert(rejectedSameIdentity, "A reset without a new DWM identity was accepted.");
        }

        private static void TestSupervisedApplyProtocol()
        {
            const string token = "0123456789ABCDEF0123456789ABCDEF";
            var activation = CreateSuccessfulSupervisedApplyResponse(token);
            var worker = CopySupervisedApplyResponse(activation);
            var commit = SupervisedApplyProtocol.CreateCommit(activation, token);

            Assert(commit.Success && commit.WatchdogArmed,
                "Creating the commit lost the successful watchdog evidence.");
            Assert(commit.CorrelationToken == token
                && commit.DwmProcessId == activation.DwmProcessId
                && commit.DwmStartTimeUtcTicks == activation.DwmStartTimeUtcTicks
                && commit.PatchActivatedUtcTicks == activation.PatchActivatedUtcTicks
                && commit.DwmCoreVersion == activation.DwmCoreVersion
                && commit.PatchPlanFingerprint == activation.PatchPlanFingerprint,
                "The commit was not bound to the exact activation identity.");
            Assert(SupervisedApplyProtocol.IsValidCommit(token, activation, worker, commit),
                "An exact activation, worker and commit handshake was rejected.");

            AssertInvalidData(
                () => SupervisedApplyProtocol.ValidateCommittedApply(token, activation, commit),
                "A commit without independent acceptance was treated as complete.");
            commit.CommitAccepted = true;
            SupervisedApplyProtocol.ValidateCommittedApply(token, activation, commit);
        }

        private static void TestSupervisedApplyProtocolRejections()
        {
            const string token = "0123456789ABCDEF0123456789ABCDEF";
            const string otherToken = "FEDCBA9876543210FEDCBA9876543210";

            AssertRejectedSupervisedMutation(token, response => response.CorrelationToken = otherToken,
                "A worker with the wrong correlation token was accepted.");
            AssertRejectedSupervisedMutation(token, response => response.DwmProcessId++,
                "A worker from a different DWM process was accepted.");
            AssertRejectedSupervisedMutation(token, response => response.DwmStartTimeUtcTicks++,
                "A worker with a different DWM start time was accepted.");
            AssertRejectedSupervisedMutation(token, response => response.PatchActivatedUtcTicks++,
                "A worker with a different patch activation timestamp was accepted.");
            AssertRejectedSupervisedMutation(token, response => response.PatchPlanFingerprint = new string('B', 64),
                "A worker with a different patch-plan hash was accepted.");
            AssertRejectedSupervisedMutation(token, response => response.DwmCoreVersion += ".tampered",
                "A worker with a different DWM module version was accepted.");
            AssertRejectedSupervisedMutation(token, response => response.WatchdogArmed = false,
                "A worker without watchdog confirmation was accepted.");
            AssertRejectedSupervisedMutation(token, response => response.Success = false,
                "A worker without a success result was accepted.");

            var activation = CreateSuccessfulSupervisedApplyResponse(token);
            var worker = CopySupervisedApplyResponse(activation);
            var commit = SupervisedApplyProtocol.CreateCommit(activation, token);
            Assert(!SupervisedApplyProtocol.IsValidCommit(otherToken, activation, worker, commit),
                "A caller-provided token different from the handshake was accepted.");

            commit.Success = false;
            Assert(!SupervisedApplyProtocol.IsValidCommit(token, activation, worker, commit),
                "An unsuccessful commit was accepted.");
            commit.Success = true;
            commit.WatchdogArmed = false;
            Assert(!SupervisedApplyProtocol.IsValidCommit(token, activation, worker, commit),
                "A commit without watchdog confirmation was accepted.");

            activation = CreateSuccessfulSupervisedApplyResponse(token);
            activation.PatchPlanFingerprint = new string('a', 64);
            Assert(!SupervisedApplyProtocol.IsValidCommit(
                    token,
                    activation,
                    CopySupervisedApplyResponse(activation),
                    CopySupervisedApplyResponse(activation)),
                "A lowercase or otherwise non-canonical SHA-256 fingerprint was accepted.");

            activation = CreateSuccessfulSupervisedApplyResponse(token);
            activation.DwmProcessId = 0;
            Assert(!SupervisedApplyProtocol.IsValidCommit(
                    token,
                    activation,
                    CopySupervisedApplyResponse(activation),
                    CopySupervisedApplyResponse(activation)),
                "An activation without a DWM process identity was accepted.");

            activation = CreateSuccessfulSupervisedApplyResponse(token);
            activation.DwmStartTimeUtcTicks = DateTime.MinValue.Ticks;
            Assert(!SupervisedApplyProtocol.IsValidCommit(
                    token,
                    activation,
                    CopySupervisedApplyResponse(activation),
                    CopySupervisedApplyResponse(activation)),
                "An activation without a DWM start time was accepted.");

            activation = CreateSuccessfulSupervisedApplyResponse(token);
            activation.PatchActivatedUtcTicks = DateTime.MinValue.Ticks;
            Assert(!SupervisedApplyProtocol.IsValidCommit(
                    token,
                    activation,
                    CopySupervisedApplyResponse(activation),
                    CopySupervisedApplyResponse(activation)),
                "An activation without a patch timestamp was accepted.");
        }

        private static void AssertRejectedSupervisedMutation(
            string token,
            Action<DwmPatchResponse> mutateResponse,
            string message)
        {
            var activation = CreateSuccessfulSupervisedApplyResponse(token);
            var worker = CopySupervisedApplyResponse(activation);
            var commit = SupervisedApplyProtocol.CreateCommit(activation, token);
            mutateResponse(activation);
            Assert(!SupervisedApplyProtocol.IsValidCommit(token, activation, worker, commit),
                message + " (activation)");

            activation = CreateSuccessfulSupervisedApplyResponse(token);
            worker = CopySupervisedApplyResponse(activation);
            commit = SupervisedApplyProtocol.CreateCommit(activation, token);
            mutateResponse(worker);
            Assert(!SupervisedApplyProtocol.IsValidCommit(token, activation, worker, commit),
                message + " (worker)");

            activation = CreateSuccessfulSupervisedApplyResponse(token);
            worker = CopySupervisedApplyResponse(activation);
            commit = SupervisedApplyProtocol.CreateCommit(activation, token);
            mutateResponse(commit);
            Assert(!SupervisedApplyProtocol.IsValidCommit(token, activation, worker, commit),
                message + " (commit)");
        }

        private static void TestDwmStatePersistence()
        {
            var directory = Path.Combine(Path.GetTempPath(), "HDRCalibrate_StateTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var store = new DwmStateStore(directory);
                var expected = new PersistedDwmState
                {
                    WasApplied = true,
                    DwmProcessId = 1234,
                    DwmStartTimeUtcTicks = 5678,
                    SessionId = 9,
                    DwmCoreVersion = "test",
                    AppliedUtc = new DateTime(638000000000000000, DateTimeKind.Utc),
                    PatchPlanFingerprint = new string('B', 64),
                    Settings = new CalibrationSettings { Gamma = 2.4, ApplySdrGammaCorrection = true }
                };
                store.Save(expected);
                var actual = store.Load();
                Assert(actual != null && actual.WasApplied, "DWM state was not loaded.");
                Assert(actual.DwmProcessId == expected.DwmProcessId, "DWM PID was not persisted.");
                Assert(actual.PatchPlanFingerprint == expected.PatchPlanFingerprint,
                    "DWM patch-plan fingerprint was not persisted.");
                AssertClose(expected.Settings.Gamma, actual.Settings.Gamma, 1e-12, "Persisted DWM gamma");
                expected.DwmProcessId = 4321;
                store.Save(expected);
                Assert(store.Load().DwmProcessId == 4321, "DWM state was not atomically replaced.");
                store.Clear();
                Assert(store.Load() == null, "DWM state was not cleared.");
            }
            finally
            {
                var statePath = Path.Combine(directory, "dwm-state.xml");
                var backupPath = statePath + ".bak";
                var temporaryPath = statePath + ".tmp";
                if (File.Exists(statePath)) File.Delete(statePath);
                if (File.Exists(backupPath)) File.Delete(backupPath);
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                if (Directory.Exists(directory)) Directory.Delete(directory, false);
            }
        }

        private static void TestStartupActivationPolicy()
        {
            var settings = new CalibrationSettings
            {
                Gamma = 2.4,
                Brightness = 0.05,
                ApplySdrGammaCorrection = true
            };
            var preflight = new DwmPatchEngine().Preflight(settings);
            Assert(preflight.Success && IsUppercaseSha256(preflight.PatchPlanFingerprint),
                "Startup policy preflight failed.");

            var timedTest = CreateSuccessfulTimedTestResponse(preflight);
            var windowsEnvironment = CreateTestWindowsEnvironment();
            var windowsEnvironmentProvider = new MutableWindowsEnvironmentIdentityProvider(windowsEnvironment);
            var policy = StartupActivationPolicy.CreateFromSuccessfulTest(
                settings,
                timedTest,
                "1.5.0.0",
                "1.5.0.0",
                false,
                windowsEnvironment,
                windowsEnvironmentProvider);
            Assert(!policy.Enabled, "A successful test silently enabled startup activation.");
            Assert(policy.WatchdogResetConfirmed, "Startup approval lost the watchdog reset proof.");
            Assert(policy.SettingsFingerprint == CalibrationSettingsIdentity.Compute(settings),
                "Startup approval is not bound to every calibration setting.");
            var settingsFingerprint = CalibrationSettingsIdentity.Compute(settings);

            var activeState = new PersistedDwmState
            {
                WasApplied = true,
                DwmProcessId = 123,
                DwmStartTimeUtcTicks = 456,
                PatchPlanFingerprint = preflight.PatchPlanFingerprint,
                Settings = settings.Clone()
            };
            Assert(StartupActivationPolicy.CanEnable(
                    policy, "1.5.0.0", "1.5.0.0", preflight, settingsFingerprint, activeState, true,
                    windowsEnvironmentProvider),
                "Exact tested and applied settings could not enable startup activation.");
            Assert(!StartupActivationPolicy.CanEnable(
                    policy, "1.5.1.0", "1.5.0.0", preflight, settingsFingerprint, activeState, true,
                    windowsEnvironmentProvider),
                "A different application version reused a startup approval.");

            activeState.Settings.Gamma = 2.2;
            Assert(!StartupActivationPolicy.CanEnable(
                    policy, "1.5.0.0", "1.5.0.0", preflight, settingsFingerprint, activeState, true,
                    windowsEnvironmentProvider),
                "Different applied settings reused a startup approval.");
            activeState.Settings = settings.Clone();

            var differentSettings = settings.Clone();
            differentSettings.Gamma = 2.2;
            var differentSettingsPreflight = new DwmPatchEngine().Preflight(differentSettings);
            Assert(differentSettingsPreflight.Success, "Mismatched-settings preflight failed unexpectedly.");
            differentSettingsPreflight.PatchPlanFingerprint = preflight.PatchPlanFingerprint;
            var differentSettingsFingerprint = CalibrationSettingsIdentity.Compute(differentSettings);
            Assert(!StartupActivationPolicy.CanEnable(
                    policy, "1.5.0.0", "1.5.0.0", differentSettingsPreflight,
                    differentSettingsFingerprint, activeState, true, windowsEnvironmentProvider),
                "A preflight for different settings was accepted after its plan hash was spoofed.");

            policy.Enabled = true;
            Assert(StartupActivationPolicy.Evaluate(
                    policy, false, "1.5.0.0", "1.5.0.0", preflight, settingsFingerprint,
                    true, false, activeState, false, windowsEnvironmentProvider) == StartupActivationDecision.NotStartupLaunch,
                "A normal launch was allowed to auto-activate DWM.");
            Assert(StartupActivationPolicy.Evaluate(
                    policy, true, "1.5.0.0", "1.5.0.0", preflight, settingsFingerprint,
                    true, false, activeState, true, windowsEnvironmentProvider) == StartupActivationDecision.AlreadyActive,
                "An already active exact DWM state was not treated as a no-op.");
            Assert(StartupActivationPolicy.Evaluate(
                    policy, true, "1.5.0.0", "1.5.0.0", preflight, settingsFingerprint,
                    true, false, activeState, false, windowsEnvironmentProvider) == StartupActivationDecision.Ready,
                "A valid startup approval was not ready when DWM needed activation.");
            Assert(StartupActivationPolicy.Evaluate(
                    policy, true, "1.5.0.0", "1.5.0.0", preflight, settingsFingerprint,
                    false, false, activeState, false, windowsEnvironmentProvider) == StartupActivationDecision.HdrUnavailable,
                "Startup activation did not fail closed without an HDR display.");
            Assert(StartupActivationPolicy.Evaluate(
                    policy, true, "1.5.0.0", "1.5.0.0", preflight, settingsFingerprint,
                    true, true, activeState, false, windowsEnvironmentProvider) == StartupActivationDecision.DisplayRestricted,
                "Startup activation ignored an Advanced Color policy restriction.");

            var blockedPreflight = CopyPreflight(preflight);
            blockedPreflight.LivePatchBlocked = true;
            Assert(StartupActivationPolicy.Evaluate(
                    policy, true, "1.5.0.0", "1.5.0.0", blockedPreflight, settingsFingerprint,
                    true, false, activeState, false, windowsEnvironmentProvider) == StartupActivationDecision.LivePatchBlocked,
                "Startup activation ignored the live-patch safety gate.");
            Assert(StartupActivationPolicy.Evaluate(
                    policy, true, "1.5.1.0", "1.5.0.0", preflight, settingsFingerprint,
                    true, false, activeState, false, windowsEnvironmentProvider) == StartupActivationDecision.ApprovalInvalid,
                "Startup activation reused approval across application versions.");
            Assert(StartupActivationPolicy.Evaluate(
                    policy, true, "1.5.0.0", "1.5.0.0", differentSettingsPreflight,
                    differentSettingsFingerprint, true, false, activeState, false, windowsEnvironmentProvider)
                    == StartupActivationDecision.ApprovalInvalid,
                "Startup activation accepted a preflight produced for different settings.");

            AssertStartupTestDuration(settings, preflight,
                TimedDwmTestRunner.DurationMilliseconds - 1, false);
            AssertStartupTestDuration(settings, preflight,
                TimedDwmTestRunner.DurationMilliseconds, true);
            AssertStartupTestDuration(settings, preflight,
                TimedDwmTestRunner.DurationMilliseconds + 1, true);
            AssertStartupTestDuration(settings, preflight,
                TimedDwmTestRunner.DurationMilliseconds + 1000, true);
            AssertStartupTestDuration(settings, preflight,
                TimedDwmTestRunner.DurationMilliseconds + 1001, false);
        }

        private static void TestStartupActivationPolicyPersistence()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "HDRCalibrate_StartupPolicyTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var store = new StartupActivationPolicyStore(directory);
            try
            {
                var settings = new CalibrationSettings
                {
                    Gamma = 2.2,
                    Brightness = -0.03,
                    ApplySdrGammaCorrection = true
                };
                var preflight = new DwmPatchEngine().Preflight(settings);
                Assert(preflight.Success, "Startup policy persistence preflight failed.");
                var windowsEnvironment = CreateTestWindowsEnvironment();
                var policy = StartupActivationPolicy.CreateFromSuccessfulTest(
                    settings,
                    CreateSuccessfulTimedTestResponse(preflight),
                    "1.5.0.0",
                    "1.5.0.0",
                    false,
                    windowsEnvironment,
                    new MutableWindowsEnvironmentIdentityProvider(windowsEnvironment));

                store.Save(policy);
                var loaded = store.Load();
                Assert(loaded != null && loaded.SettingsFingerprint == policy.SettingsFingerprint,
                    "Startup activation policy was not loaded after atomic save.");
                Assert(loaded.SchemaVersion == StartupActivationPolicy.CurrentSchemaVersion
                    && loaded.WindowsEnvironment != null
                    && loaded.WindowsEnvironment.Matches(windowsEnvironment),
                    "Atomic persistence did not round-trip the complete tested Windows identity.");
                policy.Enabled = true;
                store.Save(policy);
                Assert(store.Load().Enabled, "Enabled startup activation policy was not atomically replaced.");

                File.WriteAllText(store.PolicyPath, "<not-valid-policy>");
                var rejectedCorruption = false;
                try
                {
                    store.Load();
                }
                catch (InvalidDataException)
                {
                    rejectedCorruption = true;
                }
                Assert(rejectedCorruption, "A corrupt startup activation policy was accepted.");

                store.Clear();
                Assert(!File.Exists(store.PolicyPath)
                    && !File.Exists(store.PolicyPath + ".tmp")
                    && !File.Exists(store.PolicyPath + ".bak"),
                    "Clearing startup activation left policy files behind.");
            }
            finally
            {
                foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
                if (Directory.Exists(directory)) Directory.Delete(directory, false);
            }
        }

        private static void TestStartupActivationDisableFailure()
        {
            var windowsEnvironment = CreateTestWindowsEnvironment();
            var store = new ThrowingStartupActivationPolicyStore(
                CreateTestStartupPolicy(windowsEnvironment, true));
            var runStore = new MemoryRunValueStore();
            runStore.Write(
                AutostartService.ValueName,
                @"""C:\Apps\HDRCalibrate.exe"" --tray --startup");
            var autostartService = new AutostartService(
                runStore,
                @"C:\Apps\HDRCalibrate.exe");
            var viewModel = new MainViewModel(
                autostartService,
                store,
                new MutableWindowsEnvironmentIdentityProvider(windowsEnvironment));
            Assert(viewModel.IsStartupActivationEnabled,
                "The enabled startup activation was not loaded before the failure test.");
            Assert(viewModel.IsAutostartEnabled,
                "The Windows autostart fallback was not enabled before the failure test.");

            viewModel.ToggleStartupActivation();

            Assert(store.SaveAttempts == 2 && store.ClearAttempts == 1,
                "A failed disable did not try both the persisted false value and fail-closed removal.");
            Assert(viewModel.IsStartupActivationEnabled,
                "A failed persistent disable was displayed as successfully disabled.");
            Assert(!viewModel.IsAutostartEnabled && !autostartService.IsEnabled(),
                "A policy that could not be disabled or removed left Windows autostart enabled.");
            Assert(viewModel.IsError
                && viewModel.StatusMessage.StartsWith(
                    "Startaktivierung konnte nicht geändert werden",
                    StringComparison.Ordinal),
                "A failed persistent disable was not kept visible as an error.");

            viewModel.RefreshStartupActivationState();
            Assert(viewModel.IsStartupActivationEnabled && viewModel.IsError,
                "Refreshing after a failed disable hid the persisted enabled state or its error.");
        }

        private static void TestWindowsEnvironmentIdentity()
        {
            var original = CreateTestWindowsEnvironment();
            var same = original.Clone();
            original.Validate();
            Assert(!ReferenceEquals(original, same) && original.Matches(same) && same.Matches(original),
                "A copied Windows identity did not represent exactly the same environment.");
            Assert(!original.Matches(null), "A missing Windows environment matched a complete one.");
            same.DwmExecutableSha256 = same.DwmExecutableSha256.ToLowerInvariant();
            same.DwmCoreSha256 = same.DwmCoreSha256.ToLowerInvariant();
            Assert(original.Matches(same), "Hash letter casing changed the Windows environment identity.");

            foreach (var change in WindowsEnvironmentChanges())
            {
                var changed = original.Clone();
                change.Value(changed);
                changed.Validate();
                Assert(!original.Matches(changed) && !changed.Matches(original),
                    "Windows identity ignored a change to " + change.Key + ".");
            }
            foreach (var invalidate in InvalidWindowsEnvironmentChanges())
            {
                var invalid = original.Clone();
                invalidate.Value(invalid);
                AssertInvalidData(invalid.Validate,
                    "Malformed Windows identity accepted: " + invalidate.Key + ".");
                Assert(!original.Matches(invalid) && !invalid.Matches(invalid),
                    "Malformed Windows identities matched each other: " + invalidate.Key + ".");
            }
            AssertInvalidData(new WindowsEnvironmentIdentity().Validate,
                "An absent Windows identity serialized as default values was accepted.");
        }

        private static void TestStartupWindowsUpdateMatrix()
        {
            var environment = CreateTestWindowsEnvironment();
            var policy = CreateTestStartupPolicy(environment, true);
            var provider = new MutableWindowsEnvironmentIdentityProvider(environment);
            Assert(EvaluateTestStartupPolicy(policy, provider) == StartupActivationDecision.Ready,
                "An unchanged approved Windows environment could not activate at startup.");
            Assert(CanEnableTestStartupPolicy(policy, provider),
                "An unchanged tested and applied Windows environment could not be explicitly enabled.");

            foreach (var change in WindowsEnvironmentChanges())
            {
                var changed = environment.Clone();
                change.Value(changed);
                provider.Current = changed;
                Assert(!StartupActivationPolicy.IsValidForCurrentBuild(
                    policy, policy.AppVersion, policy.HelperVersion, provider),
                    "Startup policy validation ignored an updated " + change.Key + ".");
                Assert(EvaluateTestStartupPolicy(policy, provider) == StartupActivationDecision.ApprovalInvalid,
                    "A Windows update could reuse a startup approval: " + change.Key + ".");
                Assert(EvaluateTestStartupPolicy(policy, provider, true) == StartupActivationDecision.ApprovalInvalid,
                    "An existing active-state marker bypassed the Windows update check: " + change.Key + ".");
                Assert(!CanEnableTestStartupPolicy(policy, provider),
                    "An old test could be re-enabled after a Windows update: " + change.Key + ".");
            }
        }

        private static void TestWindowsEnvironmentGuard()
        {
            var environment = CreateTestWindowsEnvironment();
            var provider = new MutableWindowsEnvironmentIdentityProvider(environment.Clone());
            WindowsEnvironmentGuard.RequireCurrent(environment, provider);
            Assert(provider.CaptureAttempts == 1,
                "The Windows side-effect guard did not check the current environment.");
            var capturesBeforeInvalidRequest = provider.CaptureAttempts;
            AssertInvalidData(() => WindowsEnvironmentGuard.RequireCurrent(null, provider),
                "A missing expected Windows identity was accepted by the side-effect guard.");
            AssertInvalidData(() => WindowsEnvironmentGuard.RequireCurrent(new WindowsEnvironmentIdentity(), provider),
                "A malformed expected Windows identity was accepted by the side-effect guard.");
            Assert(provider.CaptureAttempts == capturesBeforeInvalidRequest,
                "Malformed Windows approval evidence was not rejected before reading the environment.");
            foreach (var change in WindowsEnvironmentChanges())
            {
                provider.Current = environment.Clone();
                change.Value(provider.Current);
                AssertInvalidData(() => WindowsEnvironmentGuard.RequireCurrent(environment, provider),
                    "The immediate pre-operation guard ignored an updated " + change.Key + ".");
            }
            provider.Current = null;
            AssertInvalidData(() => WindowsEnvironmentGuard.RequireCurrent(environment, provider),
                "A missing current Windows identity passed the immediate pre-operation guard.");
            provider.Current = new WindowsEnvironmentIdentity();
            AssertInvalidData(() => WindowsEnvironmentGuard.RequireCurrent(environment, provider),
                "An invalid current Windows identity passed the immediate pre-operation guard.");
            provider.CaptureError = new InvalidDataException("Simulierter Erfassungsfehler vor dem Helferstart");
            AssertInvalidData(() => WindowsEnvironmentGuard.RequireCurrent(environment, provider),
                "A failed identity capture passed the immediate pre-operation guard.");
        }

        private static void TestSupervisedApplyRequiresWindowsEnvironment()
        {
            var callbackInvoked = false;
            Action<DwmPatchResponse> callback = response => callbackInvoked = true;
            // Null expected identities fail synchronously before manifest inspection,
            // process creation, elevation, DWM restart, or an activation callback.
            AssertInvalidData(() => new DwmPatchService().ApplySupervised(
                CreateTestStartupSettings(), callback, null),
                "The application accepted a supervised apply with no approved Windows identity.");
            AssertInvalidData(() => new DwmPatchEngine().ApplySupervisedPersistent(
                CreateTestStartupSettings(), callback, null),
                "The privileged engine accepted a supervised apply with no approved Windows identity.");
            Assert(!callbackInvoked, "Missing Windows approval evidence reached an activation callback.");
        }

        private static void TestWindowsEnvironmentRequestRoundTrip()
        {
            var environment = CreateTestWindowsEnvironment();
            var request = new DwmPatchRequest
            {
                Command = DwmPatchCommand.SupervisedApply,
                CorrelationToken = "windows-environment-roundtrip-test",
                Settings = CreateTestStartupSettings(),
                ExpectedWindowsEnvironment = environment,
                StartupApproval = new StartupApprovalFileProof
                {
                    PolicyPath = @"C:\FakeStartupApprovalTest\dwm-startup-policy.xml",
                    Sha256 = new string('E', 64)
                }
            };
            var serializer = new System.Xml.Serialization.XmlSerializer(typeof(DwmPatchRequest));
            using (var writer = new StringWriter(CultureInfo.InvariantCulture))
            {
                serializer.Serialize(writer, request);
                using (var reader = new StringReader(writer.ToString()))
                {
                    var loaded = (DwmPatchRequest)serializer.Deserialize(reader);
                    Assert(loaded.Command == request.Command
                        && loaded.CorrelationToken == request.CorrelationToken
                        && loaded.ExpectedWindowsEnvironment != null
                        && loaded.ExpectedWindowsEnvironment.Matches(environment)
                        && loaded.StartupApproval != null
                        && loaded.StartupApproval.PolicyPath == request.StartupApproval.PolicyPath
                        && loaded.StartupApproval.Sha256 == request.StartupApproval.Sha256,
                        "The DWM request lost its expected Windows identity or durable approval proof during XML transport.");
                    Assert(!ReferenceEquals(loaded.ExpectedWindowsEnvironment, environment),
                        "The request XML test did not materialize an independent Windows identity.");
                }
            }
            using (var reader = new StringReader("<DwmPatchRequest><Command>Apply</Command></DwmPatchRequest>"))
            {
                var legacy = (DwmPatchRequest)serializer.Deserialize(reader);
                Assert(legacy.ExpectedWindowsEnvironment == null,
                    "A legacy DWM request fabricated a current Windows approval identity.");
                AssertInvalidData(() => WindowsEnvironmentGuard.RequireCurrent(
                    legacy.ExpectedWindowsEnvironment, new MutableWindowsEnvironmentIdentityProvider(environment)),
                    "A legacy DWM request without Windows identity was automatically upgraded.");
            }
        }

        private static void TestStartupApprovalFileProof()
        {
            var directory = Path.Combine(Path.GetTempPath(), "HDRCalibrate_ApprovalFileProofTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var store = new StartupActivationPolicyStore(directory);
            try
            {
                var policy = CreateTestStartupPolicy(CreateTestWindowsEnvironment(), true);
                store.Save(policy);
                var proof = StartupApprovalFileProof.Capture(store.PolicyPath);
                Assert(proof.PolicyPath == Path.GetFullPath(store.PolicyPath) && IsUppercaseSha256(proof.Sha256),
                    "Startup proof did not bind an absolute policy path to an exact SHA-256.");
                proof.RequireUnchanged();
                policy.Enabled = false;
                policy.ApprovalRevoked = true;
                store.Save(policy);
                AssertInvalidData(proof.RequireUnchanged,
                    "A policy revoked while activation was pending still passed the durable proof check.");

                var revokedProof = StartupApprovalFileProof.Capture(store.PolicyPath);
                var replacementPolicy = CreateTestStartupPolicy(CreateTestWindowsEnvironment(), false);
                store.Save(replacementPolicy);
                AssertInvalidData(revokedProof.RequireUnchanged,
                    "Replacing a policy while activation was pending was not detected.");
                var currentProof = StartupApprovalFileProof.Capture(store.PolicyPath);
                currentProof.RequireUnchanged();
                using (var lockedPolicy = new FileStream(store.PolicyPath, FileMode.Open, FileAccess.Read, FileShare.None))
                    AssertThrows<IOException>(currentProof.RequireUnchanged,
                        "An unreadable policy file was treated as unchanged.");
                store.Clear();
                AssertThrows<IOException>(currentProof.RequireUnchanged,
                    "A removed startup approval file was accepted by an in-flight activation.");

                File.WriteAllBytes(store.PolicyPath, new byte[0]);
                AssertInvalidData(() => StartupApprovalFileProof.Capture(store.PolicyPath),
                    "An empty startup approval file created a durable activation proof.");
                File.WriteAllBytes(store.PolicyPath, new byte[64 * 1024 + 1]);
                AssertInvalidData(() => StartupApprovalFileProof.Capture(store.PolicyPath),
                    "An excessive startup approval file created a durable activation proof.");
                foreach (var malformedProof in new[]
                {
                    new StartupApprovalFileProof(),
                    new StartupApprovalFileProof { PolicyPath = "relative-policy.xml", Sha256 = new string('A', 64) },
                    new StartupApprovalFileProof { PolicyPath = store.PolicyPath, Sha256 = new string('A', 63) },
                    new StartupApprovalFileProof { PolicyPath = store.PolicyPath, Sha256 = new string('Z', 64) }
                })
                    AssertInvalidData(malformedProof.RequireUnchanged,
                        "A malformed durable startup approval proof was accepted.");
            }
            finally
            {
                foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
                if (Directory.Exists(directory)) Directory.Delete(directory, false);
            }
        }

        private static void TestStartupProofCaptureRequiresEnabledPolicy()
        {
            var directory = Path.Combine(Path.GetTempPath(), "HDRCalibrate_ApprovalCaptureTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var store = new StartupActivationPolicyStore(directory);
            try
            {
                var environment = CreateTestWindowsEnvironment();
                var policy = CreateTestStartupPolicy(environment, true);
                store.Save(policy);
                var expected = store.Load();
                var provider = new MutableWindowsEnvironmentIdentityProvider(environment);
                var viewModel = CreateIsolatedViewModel(store, provider, CreateEnabledRunValueStore());
                var proof = CaptureStartupApprovalForTest(viewModel, expected);
                proof.RequireUnchanged();

                policy.Enabled = false;
                policy.ApprovalRevoked = true;
                store.Save(policy);
                AssertInvalidData(() => CaptureStartupApprovalForTest(viewModel, expected),
                    "A revocation on disk between approval evaluation and proof capture was accepted.");

                policy.Enabled = true;
                policy.ApprovalRevoked = false;
                policy.TestedUtc = expected.TestedUtc.AddTicks(1);
                store.Save(policy);
                AssertInvalidData(() => CaptureStartupApprovalForTest(viewModel, expected),
                    "Replacing the successful test between approval evaluation and proof capture was accepted.");

                store.Save(expected);
                provider.Current = environment.Clone();
                provider.Current.UpdateBuildRevision++;
                AssertInvalidData(() => CaptureStartupApprovalForTest(viewModel, expected),
                    "Windows changed after approval evaluation but proof capture still authorized activation.");

                var memoryStore = new MemoryStartupActivationPolicyStore();
                memoryStore.Save(expected);
                var memoryViewModel = CreateIsolatedViewModel(memoryStore,
                    new MutableWindowsEnvironmentIdentityProvider(environment), CreateEnabledRunValueStore());
                AssertInvalidData(() => CaptureStartupApprovalForTest(memoryViewModel, expected),
                    "A startup policy without durable file evidence authorized automatic activation.");
            }
            finally
            {
                foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
                if (Directory.Exists(directory)) Directory.Delete(directory, false);
            }
        }

        private static void TestStartupInvalidWindowsIdentity()
        {
            var environment = CreateTestWindowsEnvironment();
            var provider = new MutableWindowsEnvironmentIdentityProvider(environment);
            var mutations = new Dictionary<string, Action<PersistedStartupActivationPolicy>>
            {
                ["legacy schema"] = policy => policy.SchemaVersion = 1,
                ["missing schema"] = policy => policy.SchemaVersion = 0,
                ["unknown future schema"] = policy => policy.SchemaVersion = int.MaxValue,
                ["missing Windows environment"] = policy => policy.WindowsEnvironment = null,
                ["empty Windows environment"] = policy => policy.WindowsEnvironment = new WindowsEnvironmentIdentity(),
                ["inconsistent DWM fingerprint"] = policy => policy.WindowsEnvironment.DwmCoreSha256 = new string('E', 64),
                ["enabled revoked approval"] = policy => policy.ApprovalRevoked = true
            };
            foreach (var invalid in InvalidWindowsEnvironmentChanges())
            {
                var invalidation = invalid.Value;
                mutations.Add(invalid.Key, policy => invalidation(policy.WindowsEnvironment));
            }
            foreach (var mutation in mutations)
            {
                var policy = CreateTestStartupPolicy(environment, true);
                mutation.Value(policy);
                AssertInvalidData(() => StartupActivationPolicy.ValidateStoredPolicy(policy, true),
                    "Invalid stored Windows approval was accepted: " + mutation.Key + ".");
                Assert(EvaluateTestStartupPolicy(policy, provider) == StartupActivationDecision.ApprovalInvalid,
                    "Invalid stored Windows approval could start: " + mutation.Key + ".");
                Assert(!CanEnableTestStartupPolicy(policy, provider),
                    "Invalid stored Windows approval could be explicitly enabled: " + mutation.Key + ".");
            }

            var xml = "<WindowsEnvironmentIdentity><OsBuildNumber>26100</OsBuildNumber>"
                + "<BuildLabEx>26100.1.amd64fre.ge_release.240331-1435</BuildLabEx>"
                + "<DwmExecutableSha256>" + environment.DwmExecutableSha256 + "</DwmExecutableSha256>"
                + "<DwmCoreSha256>" + environment.DwmCoreSha256 + "</DwmCoreSha256></WindowsEnvironmentIdentity>";
            using (var reader = new StringReader(xml))
            {
                var incomplete = (WindowsEnvironmentIdentity)new System.Xml.Serialization.XmlSerializer(
                    typeof(WindowsEnvironmentIdentity)).Deserialize(reader);
                Assert(incomplete.UpdateBuildRevision == -1,
                    "A missing persisted UBR was silently treated as a valid zero revision.");
                AssertInvalidData(incomplete.Validate, "A persisted Windows identity without UBR was accepted.");
            }
        }

        private static void TestStartupWindowsIdentityCaptureFailure()
        {
            var environment = CreateTestWindowsEnvironment();
            var policy = CreateTestStartupPolicy(environment, true);
            var provider = new MutableWindowsEnvironmentIdentityProvider(environment);
            foreach (var captureError in new Exception[]
            {
                new IOException("Simulierter unlesbarer DWM-Dateifingerabdruck"),
                new UnauthorizedAccessException("Simulierter verweigerter Windows-Buildzugriff"),
                new InvalidDataException("Simulierter wechselnder Windows-Build während der Erfassung")
            })
            {
                provider.CaptureError = captureError;
                Assert(EvaluateTestStartupPolicy(policy, provider) == StartupActivationDecision.ApprovalInvalid,
                    "A failed Windows identity capture did not block automatic activation.");
                Assert(!CanEnableTestStartupPolicy(policy, provider),
                    "A failed Windows identity capture allowed re-enabling the old approval.");

                var store = new MemoryStartupActivationPolicyStore();
                store.Save(CreateTestStartupPolicy(environment, true));
                var viewModel = CreateIsolatedViewModel(store, provider, CreateEnabledRunValueStore());
                AssertPolicyRevoked(store.Load(), "A Windows identity read failure did not revoke the persisted approval.");
                Assert(!viewModel.IsStartupActivationEnabled,
                    "The UI kept automatic activation enabled after identity capture failed.");
            }
            provider.CaptureError = null;
            provider.Current = null;
            Assert(EvaluateTestStartupPolicy(policy, provider) == StartupActivationDecision.ApprovalInvalid,
                "A null captured Windows identity did not fail closed.");
            provider.Current = new WindowsEnvironmentIdentity();
            Assert(!CanEnableTestStartupPolicy(policy, provider),
                "A malformed captured Windows identity was accepted.");
        }

        private static void TestTimedTestWindowsEnvironmentBinding()
        {
            var testedEnvironment = CreateTestWindowsEnvironment();
            var settings = CreateTestStartupSettings();
            var response = CreateSuccessfulTimedTestResponse(CreateTestStartupPreflight());
            var provider = new MutableWindowsEnvironmentIdentityProvider(testedEnvironment.Clone());
            var policy = StartupActivationPolicy.CreateFromSuccessfulTest(
                settings, response, "1.5.0.0", "1.5.0.0", false, testedEnvironment, provider);
            Assert(!policy.Enabled && !policy.ApprovalRevoked,
                "A successful test silently enabled startup or retained an old revocation.");
            Assert(!ReferenceEquals(policy.WindowsEnvironment, testedEnvironment),
                "The persisted test approval retained the mutable caller's Windows snapshot.");
            testedEnvironment.UpdateBuildRevision++;
            Assert(policy.WindowsEnvironment.UpdateBuildRevision != testedEnvironment.UpdateBuildRevision,
                "Changing the caller's snapshot altered the approved Windows environment.");
            testedEnvironment = CreateTestWindowsEnvironment();

            foreach (var change in WindowsEnvironmentChanges())
            {
                provider.Current = testedEnvironment.Clone();
                change.Value(provider.Current);
                AssertInvalidData(() => StartupActivationPolicy.CreateFromSuccessfulTest(
                    settings, response, "1.5.0.0", "1.5.0.0", true, testedEnvironment, provider),
                    "A Windows change during the supervised test was approved: " + change.Key + ".");
            }
            provider.Current = testedEnvironment;
            AssertInvalidData(() => StartupActivationPolicy.CreateFromSuccessfulTest(
                settings, response, "1.5.0.0", "1.5.0.0", false, null, provider),
                "A test without a pre-test Windows snapshot created a startup approval.");
            provider.Current = null;
            AssertInvalidData(() => StartupActivationPolicy.CreateFromSuccessfulTest(
                settings, response, "1.5.0.0", "1.5.0.0", false, testedEnvironment, provider),
                "A missing post-test Windows snapshot created a startup approval.");
        }

        private static void TestStartupWindowsUpdateRevocationPersistence()
        {
            var directory = Path.Combine(Path.GetTempPath(), "HDRCalibrate_UpdateRevocationTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var environment = CreateTestWindowsEnvironment();
                var provider = new MutableWindowsEnvironmentIdentityProvider(environment);
                var store = new StartupActivationPolicyStore(directory);
                store.Save(CreateTestStartupPolicy(environment, true));
                var viewModel = CreateIsolatedViewModel(store, provider, CreateEnabledRunValueStore());
                Assert(viewModel.IsStartupActivationEnabled,
                    "The baseline valid startup approval was revoked before a Windows update occurred.");
                SetPrivateField(viewModel, "_lastTestedSettingsKey", "pre-update-test-proof");

                provider.Current = environment.Clone();
                provider.Current.UpdateBuildRevision++;
                viewModel.RefreshStartupActivationState();
                Assert(!viewModel.IsStartupActivationEnabled,
                    "The UI kept automatic activation enabled after the update revision changed.");
                AssertPolicyRevoked(store.Load(), "The Windows update revocation was not durably saved.");
                Assert(GetPrivateField(viewModel, "_lastTestedSettingsKey") == null,
                    "Windows update revocation retained the in-session successful test marker.");

                provider.Current = environment;
                var reloadedStore = new StartupActivationPolicyStore(directory);
                var restarted = CreateIsolatedViewModel(reloadedStore, provider, CreateEnabledRunValueStore());
                Assert(!restarted.IsStartupActivationEnabled && !restarted.CanToggleStartupActivation,
                    "Restarting after a Windows build rollback silently restored the old startup approval.");
                AssertPolicyRevoked(reloadedStore.Load(),
                    "Returning to the original Windows build discarded the persisted revocation.");
                Assert(!CanEnableTestStartupPolicy(reloadedStore.Load(), provider),
                    "Returning to the original Windows build made the revoked test reusable.");

                // The initialization task is completed without discovering displays or starting
                // a helper. A revoked startup must return before any activation can be attempted.
                SetPrivateField(restarted, "_initializationTask", System.Threading.Tasks.Task.CompletedTask);
                var result = restarted.TryActivateAtWindowsStartupAsync(true).GetAwaiter().GetResult();
                Assert(!result.Attempted && result.Decision != StartupActivationDecision.Ready,
                    "A revoked startup approval reached the helper activation branch.");
            }
            finally
            {
                foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
                if (Directory.Exists(directory)) Directory.Delete(directory, false);
            }
        }

        private static void TestDisabledStartupWindowsUpdateRevocation()
        {
            var environment = CreateTestWindowsEnvironment();
            var changedEnvironment = environment.Clone();
            changedEnvironment.DwmExecutableSha256 = new string('C', 64);
            var store = new MemoryStartupActivationPolicyStore();
            store.Save(CreateTestStartupPolicy(environment, false));
            var provider = new MutableWindowsEnvironmentIdentityProvider(changedEnvironment);
            var viewModel = CreateIsolatedViewModel(store, provider, CreateEnabledRunValueStore());
            Assert(!viewModel.IsStartupActivationEnabled,
                "The update check enabled a previously disabled startup policy.");
            AssertPolicyRevoked(store.Load(),
                "A Windows update did not revoke a disabled but previously successful test approval.");
            provider.Current = environment;
            viewModel.RefreshStartupActivationState();
            Assert(!CanEnableTestStartupPolicy(store.Load(), provider),
                "A disabled pre-update test could be enabled after a Windows build rollback.");

            foreach (var legacyEnabled in new[] { false, true })
            {
                var legacy = CreateTestStartupPolicy(environment, legacyEnabled);
                legacy.SchemaVersion = 1;
                legacy.WindowsEnvironment = null;
                store.Save(legacy);
                CreateIsolatedViewModel(store, provider, CreateEnabledRunValueStore());
                AssertPolicyRevoked(store.Load(),
                    "A legacy approval without Windows identity survived loading; enabled=" + legacyEnabled + ".");
            }
        }

        private static void TestStartupWindowsUpdateRevocationFailures()
        {
            var environment = CreateTestWindowsEnvironment();
            var changed = environment.Clone();
            changed.BuildLabEx += ".updated";
            var provider = new MutableWindowsEnvironmentIdentityProvider(changed);
            var clearFallbackStore = new FaultingStartupActivationPolicyStore(
                CreateTestStartupPolicy(environment, true), true, false);
            var clearedViewModel = CreateIsolatedViewModel(clearFallbackStore, provider, CreateEnabledRunValueStore());
            Assert(clearFallbackStore.SaveAttempts > 0 && clearFallbackStore.ClearAttempts > 0,
                "A failed durable revocation did not attempt fail-closed policy removal.");
            Assert(clearFallbackStore.Load() == null && !clearedViewModel.IsStartupActivationEnabled,
                "Successful policy-removal fallback left the old startup approval enabled.");

            var failedStore = new FaultingStartupActivationPolicyStore(
                CreateTestStartupPolicy(environment, true), true, true);
            var runStore = CreateEnabledRunValueStore();
            var blockedViewModel = CreateIsolatedViewModel(failedStore, provider, runStore);
            Assert(failedStore.SaveAttempts > 0 && failedStore.ClearAttempts > 0,
                "The revocation failure did not attempt both persistent fallback paths.");
            Assert(!blockedViewModel.IsAutostartEnabled && runStore.Read(AutostartService.ValueName) == null,
                "A Windows update with an unmodifiable policy left its Windows startup entry active.");
            Assert(!string.IsNullOrWhiteSpace(blockedViewModel.StartupActivationStatus),
                "The unrecoverable policy write failure was not visible in startup status.");
            SetPrivateField(blockedViewModel, "_initializationTask", System.Threading.Tasks.Task.CompletedTask);
            var result = blockedViewModel.TryActivateAtWindowsStartupAsync(true).GetAwaiter().GetResult();
            Assert(!result.Attempted && result.Decision != StartupActivationDecision.Ready,
                "A Windows update reached automatic activation after both policy persistence paths failed.");
        }

        private static void TestStartupWindowsUpdateReapproval()
        {
            var environment = CreateTestWindowsEnvironment();
            var oldPolicy = CreateTestStartupPolicy(environment, false);
            oldPolicy.ApprovalRevoked = true;
            var provider = new MutableWindowsEnvironmentIdentityProvider(environment);
            var previousCaptures = provider.CaptureAttempts;
            Assert(!StartupActivationPolicy.IsValidForCurrentBuild(
                oldPolicy, oldPolicy.AppVersion, oldPolicy.HelperVersion, provider),
                "An explicitly revoked test was reused on its original Windows environment.");
            Assert(provider.CaptureAttempts == previousCaptures,
                "A permanently revoked test unnecessarily tried to recapture Windows identity.");

            var updated = environment.Clone();
            updated.UpdateBuildRevision++;
            provider.Current = updated;
            var newPolicy = CreateTestStartupPolicy(updated, false);
            Assert(!newPolicy.Enabled && !newPolicy.ApprovalRevoked
                && newPolicy.WindowsEnvironment.Matches(updated),
                "A new successful test did not create a fresh, disabled approval for the new environment.");
            Assert(EvaluateTestStartupPolicy(newPolicy, provider) == StartupActivationDecision.Disabled,
                "A successful post-update test automatically enabled startup without explicit user action.");
            Assert(CanEnableTestStartupPolicy(newPolicy, provider),
                "A fresh successful test with exact active settings could not be explicitly enabled.");
            newPolicy.Enabled = true;
            Assert(EvaluateTestStartupPolicy(newPolicy, provider) == StartupActivationDecision.Ready,
                "Explicit activation of a fresh post-update approval remained blocked.");
            Assert(!oldPolicy.Enabled && oldPolicy.ApprovalRevoked,
                "The new test silently repaired or enabled the obsolete approval.");
        }

        private static void TestViewModelInitialization()
        {
            var viewModel = CreateIsolatedViewModel();
            viewModel.InitializeAsync().GetAwaiter().GetResult();
            Assert(viewModel.Displays.Count > 0, "View model found no active display.");
            Assert(viewModel.SelectedDisplay != null, "View model did not select a display.");
            Assert(viewModel.IsDwmCompatible, "View model did not expose the successful read-only DWM preflight.");
            Assert(viewModel.IsLivePatchBlocked == !DwmPatchSafety.LivePatchEnabled,
                "View model live-patch state differs from the safety gate.");
            if (viewModel.IsLivePatchBlocked)
            {
                Assert(!viewModel.CanApply, "Permanent Apply is enabled despite the staging safety block.");
                Assert(viewModel.CanTest == DwmPatchSafety.TimedTestEnabled,
                    "View model does not expose the supervised staging test correctly.");
            }
            Assert(!string.IsNullOrWhiteSpace(viewModel.DwmStatus), "View model has no DWM status.");
            Assert(viewModel.TestCommand != null, "View model has no five-second test command.");
            Assert(viewModel.ResetProfileCommand.CanExecute(null), "Emergency DWM reset is unavailable without a state marker.");
        }

        private static void TestStaleLegacyProfileRollback()
        {
            var directory = Path.Combine(Path.GetTempPath(), "HDRCalibrate_LegacyTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var display = new DisplayInfo();
                SetInternalProperty(display, nameof(DisplayInfo.FriendlyName), "Nicht vorhandener Testmonitor");
                SetInternalProperty(display, nameof(DisplayInfo.AdapterId), new HDRCalibrate.Native.Luid
                {
                    LowPart = 0xFA17FA17,
                    HighPart = unchecked((int)0xFA17FA17)
                });
                SetInternalProperty(display, nameof(DisplayInfo.SourceId), uint.MaxValue - 1);
                SetInternalProperty(display, nameof(DisplayInfo.TargetId), uint.MaxValue);
                SetInternalProperty(display, nameof(DisplayInfo.ActiveProfileName), null);

                var store = new ProfileStateStore(directory);
                store.Upsert(new PersistedDisplayState
                {
                    DisplayKey = display.Key,
                    OriginalProfileName = null,
                    LastAppliedProfileName = "HDRCalibrate_StaleRegressionTest.icm",
                    Settings = new CalibrationSettings()
                });

                new ProfileService(store).Reset(display);
                Assert(store.Get(display.Key) == null, "A stale legacy profile marker still blocks rollback.");
            }
            finally
            {
                foreach (var name in new[] { "state.xml", "state.xml.bak", "state.xml.tmp" })
                {
                    var path = Path.Combine(directory, name);
                    if (File.Exists(path)) File.Delete(path);
                }
                if (Directory.Exists(directory)) Directory.Delete(directory, false);
            }
        }

        private static MainViewModel CreateIsolatedViewModel(
            IStartupActivationPolicyStore policyStore = null,
            IWindowsEnvironmentIdentityProvider identityProvider = null,
            IRunValueStore runValueStore = null)
        {
            return new MainViewModel(
                new AutostartService(runValueStore ?? new MemoryRunValueStore(), @"C:\Apps\HDRCalibrate.exe"),
                policyStore ?? new MemoryStartupActivationPolicyStore(),
                identityProvider ?? new MutableWindowsEnvironmentIdentityProvider(CreateTestWindowsEnvironment()));
        }

        private static MemoryRunValueStore CreateEnabledRunValueStore()
        {
            var runStore = new MemoryRunValueStore();
            runStore.Write(AutostartService.ValueName, @"""C:\Apps\HDRCalibrate.exe"" --tray --startup");
            return runStore;
        }

        private static WindowsEnvironmentIdentity CreateTestWindowsEnvironment()
        {
            return new WindowsEnvironmentIdentity
            {
                OsBuildNumber = 26100,
                UpdateBuildRevision = 100,
                BuildLabEx = "26100.1.amd64fre.ge_release.240331-1435",
                DwmExecutableSha256 = new string('A', 64),
                DwmCoreSha256 = DwmCoreInspector.SupportedDwmCoreSha256
            };
        }

        private static Dictionary<string, Action<WindowsEnvironmentIdentity>> WindowsEnvironmentChanges()
        {
            return new Dictionary<string, Action<WindowsEnvironmentIdentity>>
            {
                ["OS build"] = identity => identity.OsBuildNumber++,
                ["update revision (UBR)"] = identity => identity.UpdateBuildRevision++,
                ["BuildLabEx"] = identity => identity.BuildLabEx += ".updated",
                ["dwm.exe SHA-256"] = identity => identity.DwmExecutableSha256 = new string('C', 64),
                ["dwmcore.dll SHA-256"] = identity => identity.DwmCoreSha256 = new string('D', 64)
            };
        }

        private static Dictionary<string, Action<WindowsEnvironmentIdentity>> InvalidWindowsEnvironmentChanges()
        {
            return new Dictionary<string, Action<WindowsEnvironmentIdentity>>
            {
                ["zero OS build"] = identity => identity.OsBuildNumber = 0,
                ["negative OS build"] = identity => identity.OsBuildNumber = -1,
                ["missing update revision"] = identity => identity.UpdateBuildRevision = -1,
                ["missing BuildLabEx"] = identity => identity.BuildLabEx = null,
                ["blank BuildLabEx"] = identity => identity.BuildLabEx = " ",
                ["padded BuildLabEx"] = identity => identity.BuildLabEx += " ",
                ["control character in BuildLabEx"] = identity => identity.BuildLabEx += "\n",
                ["excessive BuildLabEx"] = identity => identity.BuildLabEx = new string('a', 257),
                ["missing executable hash"] = identity => identity.DwmExecutableSha256 = null,
                ["short executable hash"] = identity => identity.DwmExecutableSha256 = new string('A', 63),
                ["nonhex executable hash"] = identity => identity.DwmExecutableSha256 = new string('G', 64),
                ["missing DWM core hash"] = identity => identity.DwmCoreSha256 = null,
                ["long DWM core hash"] = identity => identity.DwmCoreSha256 = new string('D', 65),
                ["nonhex DWM core hash"] = identity => identity.DwmCoreSha256 = new string('Z', 64)
            };
        }

        private static CalibrationSettings CreateTestStartupSettings()
        {
            return new CalibrationSettings { Gamma = 2.4, ApplySdrGammaCorrection = true };
        }

        private static DwmPatchResponse CreateTestStartupPreflight()
        {
            return new DwmPatchResponse
            {
                Success = true,
                TimedTestAvailable = true,
                DwmCoreVersion = DwmCoreInspector.SupportedDwmCoreVersion,
                PatchPlanFingerprint = new string('B', 64)
            };
        }

        private static PersistedStartupActivationPolicy CreateTestStartupPolicy(
            WindowsEnvironmentIdentity windowsEnvironment,
            bool enabled)
        {
            var helperPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "HDRCalibrate.DwmHelper.exe");
            Assert(File.Exists(helperPath),
                "Windows update view-model tests require the built helper in the test output directory.");
            var helperVersion = System.Diagnostics.FileVersionInfo.GetVersionInfo(helperPath).FileVersion.Trim();
            return StartupActivationPolicy.CreateFromSuccessfulTest(
                CreateTestStartupSettings(),
                CreateSuccessfulTimedTestResponse(CreateTestStartupPreflight()),
                typeof(MainViewModel).Assembly.GetName().Version.ToString(),
                helperVersion,
                enabled,
                windowsEnvironment,
                new MutableWindowsEnvironmentIdentityProvider(windowsEnvironment));
        }

        private static PersistedDwmState CreateTestStartupActiveState(PersistedStartupActivationPolicy policy)
        {
            return new PersistedDwmState
            {
                WasApplied = true,
                DwmProcessId = 123,
                DwmStartTimeUtcTicks = 456,
                PatchPlanFingerprint = policy?.PatchPlanFingerprint,
                Settings = policy?.Settings?.Clone()
            };
        }

        private static StartupActivationDecision EvaluateTestStartupPolicy(
            PersistedStartupActivationPolicy policy,
            IWindowsEnvironmentIdentityProvider provider,
            bool isActiveStateCurrent = false)
        {
            return StartupActivationPolicy.Evaluate(
                policy, true, policy?.AppVersion, policy?.HelperVersion,
                CreateTestStartupPreflight(), policy?.SettingsFingerprint,
                true, false, CreateTestStartupActiveState(policy), isActiveStateCurrent, provider);
        }

        private static bool CanEnableTestStartupPolicy(
            PersistedStartupActivationPolicy policy,
            IWindowsEnvironmentIdentityProvider provider)
        {
            return StartupActivationPolicy.CanEnable(
                policy, policy?.AppVersion, policy?.HelperVersion,
                CreateTestStartupPreflight(), policy?.SettingsFingerprint,
                CreateTestStartupActiveState(policy), true, provider);
        }

        private static void AssertPolicyRevoked(PersistedStartupActivationPolicy policy, string message)
        {
            Assert(policy == null || (!policy.Enabled && policy.ApprovalRevoked), message);
        }

        private static void SetPrivateField(object instance, string fieldName, object value)
        {
            var field = instance.GetType().GetField(fieldName,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (field == null) throw new InvalidOperationException("Missing field " + fieldName + ".");
            field.SetValue(instance, value);
        }

        private static object GetPrivateField(object instance, string fieldName)
        {
            var field = instance.GetType().GetField(fieldName,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (field == null) throw new InvalidOperationException("Missing field " + fieldName + ".");
            return field.GetValue(instance);
        }

        private static StartupApprovalFileProof CaptureStartupApprovalForTest(
            MainViewModel viewModel, PersistedStartupActivationPolicy expected)
        {
            var method = typeof(MainViewModel).GetMethod("CaptureStartupApprovalFile",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (method == null) throw new InvalidOperationException("Missing startup approval proof capture method.");
            try
            {
                return (StartupApprovalFileProof)method.Invoke(viewModel, new object[] { expected });
            }
            catch (System.Reflection.TargetInvocationException exception)
                when (exception.InnerException is InvalidDataException)
            {
                throw (InvalidDataException)exception.InnerException;
            }
        }

        private static DwmPatchResponse CreateSuccessfulTimedTestResponse(DwmPatchResponse preflight)
        {
            var activated = DateTime.UtcNow.AddMilliseconds(-5100).Ticks;
            return new DwmPatchResponse
            {
                Success = true,
                WatchdogArmed = true,
                DwmProcessId = 101,
                DwmStartTimeUtcTicks = DateTime.UtcNow.AddMinutes(-1).Ticks,
                TestedDwmProcessId = 100,
                PatchActivatedUtcTicks = activated,
                WatchdogResetUtcTicks = activated + TimeSpan.FromSeconds(5).Ticks,
                TestDurationMilliseconds = TimedDwmTestRunner.DurationMilliseconds,
                DwmCoreVersion = preflight.DwmCoreVersion,
                PatchPlanFingerprint = preflight.PatchPlanFingerprint
            };
        }

        private static void AssertStartupTestDuration(
            CalibrationSettings settings,
            DwmPatchResponse preflight,
            int durationMilliseconds,
            bool expectedAccepted)
        {
            var timedTest = CreateSuccessfulTimedTestResponse(preflight);
            timedTest.TestDurationMilliseconds = durationMilliseconds;
            var accepted = true;
            try
            {
                var windowsEnvironment = CreateTestWindowsEnvironment();
                StartupActivationPolicy.CreateFromSuccessfulTest(
                    settings, timedTest, "1.5.0.0", "1.5.0.0", false,
                    windowsEnvironment,
                    new MutableWindowsEnvironmentIdentityProvider(windowsEnvironment));
            }
            catch (InvalidDataException)
            {
                accepted = false;
            }
            Assert(accepted == expectedAccepted,
                "Startup test duration " + durationMilliseconds + " ms was "
                + (accepted ? "accepted" : "rejected") + " unexpectedly.");
        }

        private static DwmPatchResponse CreateSuccessfulSupervisedApplyResponse(string token)
        {
            return new DwmPatchResponse
            {
                Success = true,
                WatchdogArmed = true,
                CorrelationToken = token,
                DwmProcessId = 4242,
                DwmStartTimeUtcTicks = new DateTime(2026, 9, 4, 8, 0, 0, DateTimeKind.Utc).Ticks,
                DwmCoreVersion = DwmCoreInspector.SupportedDwmCoreVersion,
                PatchActivatedUtcTicks = new DateTime(2026, 9, 4, 8, 0, 1, DateTimeKind.Utc).Ticks,
                PatchPlanFingerprint = new string('A', 64)
            };
        }

        private static DwmPatchResponse CopySupervisedApplyResponse(DwmPatchResponse source)
        {
            return new DwmPatchResponse
            {
                Success = source.Success,
                WatchdogArmed = source.WatchdogArmed,
                CommitAccepted = source.CommitAccepted,
                CorrelationToken = source.CorrelationToken,
                DwmProcessId = source.DwmProcessId,
                DwmStartTimeUtcTicks = source.DwmStartTimeUtcTicks,
                DwmCoreVersion = source.DwmCoreVersion,
                PatchActivatedUtcTicks = source.PatchActivatedUtcTicks,
                PatchPlanFingerprint = source.PatchPlanFingerprint,
                Message = source.Message
            };
        }

        private static void AssertInvalidData(Action action, string message)
        {
            var rejected = false;
            try
            {
                action();
            }
            catch (InvalidDataException)
            {
                rejected = true;
            }
            Assert(rejected, message);
        }

        private static void AssertThrows<TException>(Action action, string message) where TException : Exception
        {
            var rejected = false;
            try
            {
                action();
            }
            catch (TException)
            {
                rejected = true;
            }
            Assert(rejected, message);
        }

        private static DwmPatchResponse CopyPreflight(DwmPatchResponse source)
        {
            return new DwmPatchResponse
            {
                Success = source.Success,
                LivePatchBlocked = source.LivePatchBlocked,
                TimedTestAvailable = source.TimedTestAvailable,
                Message = source.Message,
                DwmCoreVersion = source.DwmCoreVersion,
                PatchedShaderCount = source.PatchedShaderCount,
                PatchedConstantVectorCount = source.PatchedConstantVectorCount,
                ChangedByteCount = source.ChangedByteCount,
                ModifiedPageCount = source.ModifiedPageCount,
                PatchPlanFingerprint = source.PatchPlanFingerprint
            };
        }

        private static bool IsUppercaseSha256(string value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && value.Length == 64
                && value.All(character =>
                    (character >= '0' && character <= '9')
                    || (character >= 'A' && character <= 'F'));
        }

        private static byte[] MapPortableExecutableImage(byte[] fileImage)
        {
            if (fileImage == null || fileImage.Length < 0x40)
                throw new InvalidDataException("The PE file image is incomplete.");
            var peOffset = BitConverter.ToInt32(fileImage, 0x3C);
            if (peOffset < 0
                || peOffset > fileImage.Length - 24
                || BitConverter.ToUInt32(fileImage, peOffset) != 0x00004550)
                throw new InvalidDataException("The DWM module has no valid PE header.");

            var sectionCount = BitConverter.ToUInt16(fileImage, peOffset + 6);
            var optionalHeaderSize = BitConverter.ToUInt16(fileImage, peOffset + 20);
            var optionalHeaderOffset = peOffset + 24;
            if (optionalHeaderOffset > fileImage.Length - optionalHeaderSize
                || optionalHeaderSize < 64)
                throw new InvalidDataException("The DWM PE optional header is incomplete.");

            var sizeOfImage = checked((int)BitConverter.ToUInt32(fileImage, optionalHeaderOffset + 56));
            var sizeOfHeaders = checked((int)BitConverter.ToUInt32(fileImage, optionalHeaderOffset + 60));
            if (sizeOfImage <= 0 || sizeOfImage > 256 * 1024 * 1024
                || sizeOfHeaders <= 0 || sizeOfHeaders > fileImage.Length)
                throw new InvalidDataException("The DWM PE image dimensions are invalid.");

            var mappedImage = new byte[sizeOfImage];
            Buffer.BlockCopy(fileImage, 0, mappedImage, 0, Math.Min(sizeOfHeaders, mappedImage.Length));
            var sectionOffset = optionalHeaderOffset + optionalHeaderSize;
            for (var index = 0; index < sectionCount; index++)
            {
                var headerOffset = checked(sectionOffset + index * 40);
                if (headerOffset < 0 || headerOffset > fileImage.Length - 40)
                    throw new InvalidDataException("The DWM PE section table is incomplete.");
                var virtualAddress = checked((int)BitConverter.ToUInt32(fileImage, headerOffset + 12));
                var rawSize = checked((int)BitConverter.ToUInt32(fileImage, headerOffset + 16));
                var rawOffset = checked((int)BitConverter.ToUInt32(fileImage, headerOffset + 20));
                if (rawSize == 0) continue;
                if (virtualAddress < 0 || virtualAddress > mappedImage.Length - rawSize
                    || rawOffset < 0 || rawOffset > fileImage.Length - rawSize)
                    throw new InvalidDataException("A DWM PE section lies outside its image.");
                Buffer.BlockCopy(fileImage, rawOffset, mappedImage, virtualAddress, rawSize);
            }
            return mappedImage;
        }

        private static string ComputePatchPlanFingerprint(
            byte[] modifiedImage,
            IEnumerable<DwmWriteRange> ranges)
        {
            var type = typeof(DwmPatchEngine).Assembly.GetType(
                "HDRCalibrate.Dwm.DwmPatchPlanFingerprint",
                true);
            var method = type.GetMethod(
                "Compute",
                System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic);
            if (method == null)
                throw new InvalidOperationException("The DWM patch-plan fingerprint method is unavailable.");
            return (string)method.Invoke(null, new object[] { modifiedImage, ranges });
        }

        private static void SetInternalProperty(object instance, string propertyName, object value)
        {
            var property = instance.GetType().GetProperty(propertyName);
            if (property == null) throw new InvalidOperationException("Missing property " + propertyName + ".");
            property.SetValue(instance, value, null);
        }

        private sealed class MutableWindowsEnvironmentIdentityProvider : IWindowsEnvironmentIdentityProvider
        {
            public MutableWindowsEnvironmentIdentityProvider(WindowsEnvironmentIdentity current)
            {
                Current = current;
            }

            public WindowsEnvironmentIdentity Current { get; set; }
            public Exception CaptureError { get; set; }
            public int CaptureAttempts { get; private set; }

            public WindowsEnvironmentIdentity Capture()
            {
                CaptureAttempts++;
                if (CaptureError != null) throw CaptureError;
                return Current?.Clone();
            }
        }

        private sealed class FaultingStartupActivationPolicyStore : IStartupActivationPolicyStore
        {
            private PersistedStartupActivationPolicy _policy;
            private readonly bool _failSave;
            private readonly bool _failClear;

            public FaultingStartupActivationPolicyStore(
                PersistedStartupActivationPolicy policy, bool failSave, bool failClear)
            {
                _policy = policy;
                _failSave = failSave;
                _failClear = failClear;
            }

            public int SaveAttempts { get; private set; }
            public int ClearAttempts { get; private set; }

            public PersistedStartupActivationPolicy Load()
            {
                return _policy;
            }

            public void Save(PersistedStartupActivationPolicy policy)
            {
                SaveAttempts++;
                if (_failSave) throw new IOException("Simulierter persistenter Widerrufs-Schreibfehler");
                _policy = policy;
            }

            public void Clear()
            {
                ClearAttempts++;
                if (_failClear) throw new IOException("Simulierter persistenter Widerrufs-Löschfehler");
                _policy = null;
            }
        }

        private sealed class MemoryRunValueStore : IRunValueStore
        {
            public string ValueName { get; private set; }
            public string Value { get; set; }

            public string Read(string valueName)
            {
                return string.Equals(ValueName, valueName, StringComparison.Ordinal) ? Value : null;
            }

            public void Write(string valueName, string value)
            {
                ValueName = valueName;
                Value = value;
            }

            public void Delete(string valueName)
            {
                if (!string.Equals(ValueName, valueName, StringComparison.Ordinal)) return;
                ValueName = null;
                Value = null;
            }
        }

        private sealed class ThrowingRunValueStore : IRunValueStore
        {
            public string Read(string valueName)
            {
                throw new InvalidOperationException("Testzugriff verweigert");
            }

            public void Write(string valueName, string value)
            {
                throw new InvalidOperationException("Testzugriff verweigert");
            }

            public void Delete(string valueName)
            {
                throw new InvalidOperationException("Testzugriff verweigert");
            }
        }

        private sealed class MemoryStartupActivationPolicyStore : IStartupActivationPolicyStore
        {
            public PersistedStartupActivationPolicy Policy { get; private set; }

            public PersistedStartupActivationPolicy Load()
            {
                return Policy;
            }

            public void Save(PersistedStartupActivationPolicy policy)
            {
                Policy = policy;
            }

            public void Clear()
            {
                Policy = null;
            }
        }

        private sealed class ThrowingStartupActivationPolicyStore : IStartupActivationPolicyStore
        {
            private readonly PersistedStartupActivationPolicy _policy;

            public ThrowingStartupActivationPolicyStore(PersistedStartupActivationPolicy policy)
            {
                _policy = policy;
            }

            public int SaveAttempts { get; private set; }
            public int ClearAttempts { get; private set; }

            public PersistedStartupActivationPolicy Load()
            {
                return _policy;
            }

            public void Save(PersistedStartupActivationPolicy policy)
            {
                SaveAttempts++;
                throw new IOException("Simulierter persistenter Schreibfehler");
            }

            public void Clear()
            {
                ClearAttempts++;
                throw new IOException("Simulierter persistenter Löschfehler");
            }
        }

        private static void AssertChunkBytesEqual(
            byte[] left,
            DxbcChunk leftChunk,
            byte[] right,
            DxbcChunk rightChunk,
            string message)
        {
            Assert(leftChunk != null && rightChunk != null && leftChunk.DataSize == rightChunk.DataSize, message);
            for (var index = 0; index < leftChunk.DataSize; index++)
                Assert(left[leftChunk.DataOffset + index] == right[rightChunk.DataOffset + index], message);
        }

        private static byte[] CreateFallback()
        {
            return IccProfileFactory.CreateFallbackProfile(
                "HDR Calibrate Testprofil",
                new DisplayColorMetadata(),
                new CalibrationSettings(),
                1024,
                203.0);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void AssertClose(double expected, double actual, double tolerance, string message)
        {
            if (Math.Abs(expected - actual) > tolerance)
                throw new InvalidOperationException($"{message}: expected {expected}, actual {actual}, tolerance {tolerance}.");
        }
    }
}
