using HDRCalibrate.Core;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HDRCalibrate.Dwm
{
    /// <summary>
    /// Pure validation/aggregation for the supervised test protocol. Process lifetime,
    /// the monotonic deadline and recovery are owned by independent helper processes.
    /// </summary>
    public static class TimedDwmTestRunner
    {
        public const int DurationMilliseconds = 5000;
        public const int SetupDeadlineMilliseconds = 8000;
        public const int SupervisorGraceMilliseconds = 250;

        public static void ValidateSettings(CalibrationSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            settings.Validate();
            DwmShaderRewriter.ValidateSupportedSettings(settings);
            if (settings.IsNeutral())
                throw new InvalidOperationException("Für den Test ist mindestens eine aktive SDR-Korrektur erforderlich.");
        }

        public static bool IsValidCorrelationToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token) || token.Length != 32) return false;
            return token.All(character =>
                (character >= '0' && character <= '9')
                || (character >= 'a' && character <= 'f')
                || (character >= 'A' && character <= 'F'));
        }

        public static int GetRemainingDurationMilliseconds(long activatedUtcTicks, DateTime utcNow)
        {
            if (activatedUtcTicks <= DateTime.MinValue.Ticks || activatedUtcTicks >= DateTime.MaxValue.Ticks)
                return DurationMilliseconds;
            var activated = new DateTime(activatedUtcTicks, DateTimeKind.Utc);
            var elapsed = Math.Max(0.0, (utcNow - activated).TotalMilliseconds);
            return checked((int)Math.Max(0.0, Math.Ceiling(DurationMilliseconds - elapsed)));
        }

        public static DwmPatchResponse Combine(
            DwmPatchResponse applied,
            DwmPatchResponse reset,
            int measuredDurationMilliseconds)
        {
            EnsureSuccessful(applied, "Die temporäre DWM-Korrektur wurde nicht bestätigt.");
            EnsureSuccessful(reset, "Der unabhängige Sicherheits-Reset wurde nicht bestätigt.");
            if (measuredDurationMilliseconds < 0
                || measuredDurationMilliseconds > DurationMilliseconds + 1000)
                throw new InvalidOperationException("Die gemessene Testdauer liegt außerhalb der Sicherheitsgrenze.");
            if (applied.DwmProcessId <= 0 || applied.DwmStartTimeUtcTicks <= 0)
                throw new InvalidOperationException("Die Identität des getesteten DWM-Prozesses fehlt.");
            if (reset.DwmProcessId <= 0 || reset.DwmStartTimeUtcTicks <= 0)
                throw new InvalidOperationException("Die Identität des zurückgesetzten DWM-Prozesses fehlt.");
            if (applied.DwmProcessId == reset.DwmProcessId
                && applied.DwmStartTimeUtcTicks == reset.DwmStartTimeUtcTicks)
                throw new InvalidOperationException("Der Sicherheits-Reset hat keinen neuen DWM-Prozess erzeugt.");

            var diagnostics = new List<string>();
            AddDiagnostics(diagnostics, "Test: ", applied.Diagnostics);
            diagnostics.Add("Unabhängiger Watchdog: Rücksetzung nach " + measuredDurationMilliseconds + " ms bestätigt.");
            AddDiagnostics(diagnostics, "Reset: ", reset.Diagnostics);

            return new DwmPatchResponse
            {
                Success = true,
                Message = "5-Sekunden-Test abgeschlossen; der unabhängige Watchdog hat DWM ungepatcht neu gestartet.",
                DwmProcessId = reset.DwmProcessId,
                DwmStartTimeUtcTicks = reset.DwmStartTimeUtcTicks,
                DwmCoreVersion = reset.DwmCoreVersion ?? applied.DwmCoreVersion,
                PatchedShaderCount = applied.PatchedShaderCount,
                PatchedConstantVectorCount = applied.PatchedConstantVectorCount,
                ChangedByteCount = applied.ChangedByteCount,
                ModifiedPageCount = applied.ModifiedPageCount,
                PatchActivatedUtcTicks = applied.PatchActivatedUtcTicks,
                WatchdogResetUtcTicks = reset.WatchdogResetUtcTicks,
                WatchdogArmed = true,
                TestedDwmProcessId = applied.DwmProcessId,
                TestDurationMilliseconds = measuredDurationMilliseconds,
                Diagnostics = diagnostics
            };
        }

        private static void EnsureSuccessful(DwmPatchResponse response, string message)
        {
            if (response == null) throw new InvalidOperationException(message + " Der Helfer lieferte keine Antwort.");
            if (!response.Success) throw new InvalidOperationException(message + " " + response.Message);
        }

        private static void AddDiagnostics(ICollection<string> target, string prefix, IEnumerable<string> diagnostics)
        {
            if (diagnostics == null) return;
            foreach (var item in diagnostics.Where(value => !string.IsNullOrWhiteSpace(value)))
                target.Add(prefix + item);
        }
    }
}
