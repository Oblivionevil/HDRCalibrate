using HDRCalibrate.Core;
using HDRCalibrate.Dwm;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml;
using System.Xml.Serialization;

namespace HDRCalibrate.Services
{
    public sealed class DwmPatchService
    {
        private const int HelperTimeoutMilliseconds = 60000;
        private const int TimedTestHelperTimeoutMilliseconds = 30000;
        private static readonly TimeSpan RestartCooldown = TimeSpan.FromSeconds(10);
        private readonly XmlSerializer _requestSerializer = new XmlSerializer(typeof(DwmPatchRequest));
        private readonly XmlSerializer _responseSerializer = new XmlSerializer(typeof(DwmPatchResponse));

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
            if (state.DwmStartTimeUtcTicks == 0) return true;
            var currentStart = TryGetProcessStartTimeUtcTicks(state.DwmProcessId);
            return currentStart != 0 && currentStart == state.DwmStartTimeUtcTicks;
        }

        private DwmPatchResponse InvokeElevated(DwmPatchCommand command, CalibrationSettings settings)
        {
            var helperPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "HDRCalibrate.DwmHelper.exe");
            if (!File.Exists(helperPath))
                throw new FileNotFoundException("Der erhöhte DWM-Helfer fehlt neben HDRCalibrate.exe.", helperPath);

            var requestRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HDRCalibrate",
                "Requests");
            Directory.CreateDirectory(requestRoot);
            if (command != DwmPatchCommand.SafetyReset && command != DwmPatchCommand.Preflight)
                EnforceAndMarkRestartCooldown(Path.GetDirectoryName(requestRoot));
            var token = Guid.NewGuid().ToString("N");
            var requestPath = Path.Combine(requestRoot, "request-" + token + ".xml");
            var responsePath = Path.Combine(requestRoot, "response-" + token + ".xml");
            var helperStarted = false;
            var helperCompleted = false;

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
                    Arguments = "--request " + QuoteArgument(requestPath) + " --response " + QuoteArgument(responsePath),
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
                    throw new OperationCanceledException("Die Administratorabfrage wurde abgebrochen.", ex);
                }

                if (helper == null) throw new InvalidOperationException("Der DWM-Helfer konnte nicht gestartet werden.");
                using (helper)
                {
                    helperStarted = true;
                    var timeout = command == DwmPatchCommand.TimedTest
                        ? TimedTestHelperTimeoutMilliseconds
                        : HelperTimeoutMilliseconds;
                    helperCompleted = helper.WaitForExit(timeout);
                    if (!helperCompleted)
                    {
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
                // On timeout the elevated helper may still need both files. Leave them in place.
                if (!helperStarted || helperCompleted)
                {
                    TryDelete(requestPath);
                    TryDelete(responsePath);
                    TryDelete(responsePath + ".tmp");
                }
            }
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static void EnforceAndMarkRestartCooldown(string stateRoot)
        {
            var path = Path.Combine(stateRoot, "last-dwm-restart.txt");
            if (File.Exists(path))
            {
                DateTime lastAttempt;
                if (!long.TryParse(File.ReadAllText(path), out var ticks)
                    || ticks <= DateTime.MinValue.Ticks
                    || ticks >= DateTime.MaxValue.Ticks)
                    lastAttempt = File.GetLastWriteTimeUtc(path);
                else
                    lastAttempt = new DateTime(ticks, DateTimeKind.Utc);

                var remaining = RestartCooldown - (DateTime.UtcNow - lastAttempt);
                if (remaining > TimeSpan.Zero)
                {
                    throw new InvalidOperationException(
                        "Zum Schutz der Anzeige ist der nächste DWM-Neustart erst in " +
                        Math.Ceiling(remaining.TotalSeconds) + " Sekunden möglich.");
                }
            }
            File.WriteAllText(path, DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
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
