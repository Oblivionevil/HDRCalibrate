using HDRCalibrate.Core;
using HDRCalibrate.Dwm;
using HDRCalibrate.Services;
using HDRCalibrate.ViewModels;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

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
                Run("Timed DWM protocol validation", TestTimedDwmTestSequence);
                Run("Timed DWM recovery result validation", TestTimedDwmTestTimingCompensation);
                Run("DWM command contract defaults to Unknown", TestTimedDwmTestFailureReset);
                Run("DWM state persistence", TestDwmStatePersistence);
                Run("WPF view model preflight initialization", TestViewModelInitialization);
                Run("Stale legacy profile rollback is idempotent", TestStaleLegacyProfileRollback);

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
            var viewModel = new MainViewModel();
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
                ModifiedPageCount = 6
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
                    Settings = new CalibrationSettings { Gamma = 2.4, ApplySdrGammaCorrection = true }
                };
                store.Save(expected);
                var actual = store.Load();
                Assert(actual != null && actual.WasApplied, "DWM state was not loaded.");
                Assert(actual.DwmProcessId == expected.DwmProcessId, "DWM PID was not persisted.");
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

        private static void TestViewModelInitialization()
        {
            var viewModel = new MainViewModel();
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

        private static void SetInternalProperty(object instance, string propertyName, object value)
        {
            var property = instance.GetType().GetProperty(propertyName);
            if (property == null) throw new InvalidOperationException("Missing property " + propertyName + ".");
            property.SetValue(instance, value, null);
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
