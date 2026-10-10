using HDRCalibrate.Services;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Threading;
using System.Xml;

namespace HDRCalibrate.Tests
{
    internal static class ProgramGammaNativeContractTests
    {
        private const string ExePath = @"C:\Games\Any Application\game.exe";
        private const string DllPath = @"C:\HDR Calibrate\native\HDRCalibrate.SelectiveDwmHook.dll";
        private const string PipeName = "HDRCalibrate.ProgramGamma.0123456789abcdef0123456789abcdef";
        private const string CompleteJson = "{\"event\":\"complete\",\"started\":true,\"armed\":true,"
            + "\"target_identity_retained\":true,\"dwm_identity_retained\":true,\"stop_completed\":true,"
            + "\"stop_status\":0,\"vetoed_candidates\":120,\"read_faults\":0,\"active_callbacks\":0,\"no_post_expiry_veto\":true}";

        private static ProgramGammaTarget Target() => new ProgramGammaTarget(ExePath, 4567, 134173584000000000, 65538, "Any SDR program");

        private static string Arguments(ProgramGammaTarget target = null, string dll = DllPath, string pipe = PipeName,
            bool continuous = false, int dwmPid = 1234, long dwmCreation = 134173584000000001,
            int ownerPid = 3333, long ownerCreation = 134173584000000002)
            => ProgramGammaArguments.Build(target ?? Target(), dwmPid, dwmCreation, ownerPid, ownerCreation, dll, pipe, continuous);

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void Reject(Action action)
        {
            try { action(); }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidDataException
                || ex is SerializationException || ex is XmlException || ex is FormatException) { return; }
            throw new InvalidOperationException("Invalid native arguments or reply were accepted.");
        }

        public static void ArgumentIdentityAndDuration()
        {
            var previous = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                var test = Arguments();
                var active = Arguments(continuous: true);
                Check(test == "--veto --duration-ms 5000 --dll \"" + DllPath + "\" --target-pid 4567 --target-hwnd 65538 --target-exe \""
                    + ExePath + "\" --target-creation-filetime 134173584000000000 --expected-dwm-pid 1234 "
                    + "--expected-dwm-creation-filetime 134173584000000001 --owner-pid 3333 "
                    + "--owner-creation-filetime 134173584000000002 --pipe " + PipeName,
                    "The bounded request did not contain exactly five seconds and all target/DWM/owner identities.");
                Check(active == test + " --continuous", "Continuous activation was not an explicit extra option.");
            }
            finally { Thread.CurrentThread.CurrentCulture = previous; }

            Reject(() => Arguments(dwmPid: 0));
            Reject(() => Arguments(dwmPid: -1));
            Reject(() => Arguments(dwmPid: 4567));
            Reject(() => Arguments(dwmCreation: 0));
            Reject(() => Arguments(ownerPid: 0));
            Reject(() => Arguments(ownerPid: 4567));
            Reject(() => Arguments(ownerCreation: 0));
            Reject(() => ProgramGammaArguments.Build(null, 1234, 1, 3333, 2, DllPath, PipeName, false));
            foreach (var pipe in new[] { null, "", "HDRCalibrate.ProgramGamma.0123", PipeName + "x", PipeName + "\n", "other." + PipeName })
                Reject(() => Arguments(pipe: pipe));
            foreach (var dll in new[] { null, "", "HDRCalibrate.SelectiveDwmHook.dll", @"C:\x\other.dll", DllPath + "\"", "C:\\x\n\\HDRCalibrate.SelectiveDwmHook.dll" })
                Reject(() => Arguments(dll: dll));
            Reject(() => new ProgramGammaTarget(ExePath, 0, 1, 2, "invalid"));
            Reject(() => new ProgramGammaTarget(ExePath, 1, 0, 2, "invalid"));
            Reject(() => new ProgramGammaTarget(ExePath, 1, 2, 0, "invalid"));
            Reject(() => new ProgramGammaTarget("game.exe", 1, 2, 3, "relative"));
            Check(Target().IdentityKey == new ProgramGammaTarget(ExePath.ToUpperInvariant(), 4567,
                134173584000000000, 65538, "different title").IdentityKey, "Path case or display title changed the executable identity.");
        }

        public static void NativeCompletionGates()
        {
            var valid = ProgramGammaNativeEvent.Parse(CompleteJson);
            Check(valid.ConfirmsSafeCompletion(0) && valid.ConfirmsWithdrawal, "Complete safe withdrawal was not accepted.");
            Check(!valid.ConfirmsSafeCompletion(20), "Failure exit code was ignored.");
            foreach (var field in new[]
            {
                "\"started\":true,", "\"armed\":true,", "\"target_identity_retained\":true,",
                "\"dwm_identity_retained\":true,", "\"stop_completed\":true,", "\"stop_status\":0,",
                "\"vetoed_candidates\":120,", "\"read_faults\":0,", "\"active_callbacks\":0,",
                ",\"no_post_expiry_veto\":true"
            })
            {
                var missing = ProgramGammaNativeEvent.Parse(CompleteJson.Replace(field, string.Empty));
                Check(!missing.ConfirmsSafeCompletion(0), "Missing completion field was treated as success: " + field);
            }
            foreach (var change in new Action<ProgramGammaNativeEvent>[]
            {
                item => item.Started = false,
                item => item.Armed = false,
                item => item.TargetRetained = false,
                item => item.DwmRetained = false,
                item => item.StopCompleted = false,
                item => item.StopStatus = 1,
                item => item.VetoedCandidates = 0,
                item => item.ReadFaults = 1,
                item => item.ActiveCallbacks = 1,
                item => item.NoPostExpiryVeto = false
            })
            {
                var bad = ProgramGammaNativeEvent.Parse(CompleteJson);
                change(bad);
                Check(!bad.ConfirmsSafeCompletion(0), "A faulty completion remained eligible for test approval.");
            }

            var target = Target();
            Check(FreshTelemetry().ConfirmsActiveSelectionFor(target), "A fresh selected SDR veto was not recognized.");
            foreach (var change in new Action<ProgramGammaNativeEvent>[]
            {
                item => item.Vetoed = 0,
                item => item.ReadFaults = 1,
                item => item.SampleValid = null,
                item => item.SampleValid = false,
                item => item.Point = 2,
                item => item.Decision = 15,
                item => item.Decision = 13,
                item => item.ColorSpace = 12,
                item => item.ColorSpace = 1,
                item => item.ColorSpace = null,
                item => item.PixelFormat = 10,
                item => item.PixelFormat = 24,
                item => item.SampleQpc = 0,
                item => item.SampleQpc = item.Qpc + 1,
                item => item.SampleQpc = item.Qpc - Stopwatch.Frequency,
                item => { item.Qpc = Stopwatch.GetTimestamp() - 10 * Stopwatch.Frequency; item.SampleQpc = item.Qpc; },
                item => { item.Qpc = Stopwatch.GetTimestamp() + 10 * Stopwatch.Frequency; item.SampleQpc = item.Qpc; },
                item => item.OwnerPid = target.ProcessId + 1,
                item => item.WindowHandle = target.WindowHandle + 1
            })
            {
                var bad = FreshTelemetry();
                change(bad);
                Check(!bad.ConfirmsActiveSelectionFor(target), "Cumulative counters, HDR, stale metadata or another target confirmed current SDR selection.");
            }
            foreach (var format in new[] { 28, 87, 88 })
            {
                var sdr = FreshTelemetry();
                sdr.PixelFormat = format;
                Check(sdr.ConfirmsActiveSelectionFor(target), "An explicitly supported SDR format was rejected.");
            }
        }

        private static ProgramGammaNativeEvent FreshTelemetry()
        {
            var now = Stopwatch.GetTimestamp();
            return new ProgramGammaNativeEvent
            {
                Event = "telemetry", Phase = "veto", Vetoed = 100, ReadFaults = 0,
                SampleValid = true, Point = 1, Decision = 1, ColorSpace = 0, PixelFormat = 87,
                Qpc = now, SampleQpc = now, OwnerPid = 4567, WindowHandle = 65538
            };
        }

        public static void JsonReplyValidation()
        {
            foreach (var invalid in new[] { null, "", "{", "null", "{}", "{\"started\":true}", new string(' ', 8193),
                CompleteJson.Replace("\"started\":true", "\"started\":{}"),
                CompleteJson.Replace("\"vetoed_candidates\":120", "\"vetoed_candidates\":9223372036854775808") })
                Reject(() => ProgramGammaNativeEvent.Parse(invalid));
            var withExtraNativeDiagnostics = ProgramGammaNativeEvent.Parse(CompleteJson.TrimEnd('}')
                + ",\"gamma_applied\":false,\"vrr_confirmed\":false,\"game_writes\":0}");
            Check(withExtraNativeDiagnostics.ConfirmsSafeCompletion(0),
                "Separate unverified gamma/VRR diagnostics invalidated the safe selection result.");
            var incomplete = ProgramGammaNativeEvent.Parse("{\"event\":\"complete\",\"stop_completed\":true}");
            Check(!incomplete.ConfirmsWithdrawal && !incomplete.ConfirmsSafeCompletion(0),
                "An incomplete JSON response was treated as a complete withdrawal.");
        }
    }
}
