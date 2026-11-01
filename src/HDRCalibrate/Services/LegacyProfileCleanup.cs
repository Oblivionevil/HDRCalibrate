using System;
using System.Collections.Generic;
using System.Linq;

namespace HDRCalibrate.Services
{
    public sealed class LegacyProfileCleanupResult
    {
        public LegacyProfileCleanupResult(int restored, IReadOnlyList<string> failures)
        {
            Restored = restored;
            Failures = failures ?? new string[0];
        }

        public int Restored { get; }
        public IReadOnlyList<string> Failures { get; }

        /// <summary>Status suffix such as "; 1 alte MHC2-Zuordnung bereinigt".</summary>
        public string Format()
        {
            var text = Restored <= 0 ? string.Empty : "; " + Restored + " alte MHC2-Zuordnung bereinigt";
            if (Failures.Count > 0)
                text += "; Warnung: alte MHC2-Zuordnung nicht bereinigt (" + string.Join(" | ", Failures) +
                    "). Dort kann die SDR-Korrektur doppelt wirken; bitte die Windows-Farbverwaltung prüfen";
            return text;
        }
    }

    /// <summary>
    /// Removes MHC2 profile associations left by HDR Calibrate 1.0. This is
    /// cleanup only: a profile Windows refuses to restore must never block the
    /// DWM test, Apply or the emergency reset. The restore point is kept, so a
    /// later run retries it.
    /// </summary>
    public static class LegacyProfileCleanup
    {
        public static LegacyProfileCleanupResult Run(
            Func<IEnumerable<DisplayInfo>> getDisplays,
            Func<DisplayInfo, bool> hasRestorePoint,
            Action<DisplayInfo> reset)
        {
            if (getDisplays == null) throw new ArgumentNullException(nameof(getDisplays));
            if (hasRestorePoint == null) throw new ArgumentNullException(nameof(hasRestorePoint));
            if (reset == null) throw new ArgumentNullException(nameof(reset));
            var restored = 0;
            var failures = new List<string>();
            List<DisplayInfo> displays;
            try { displays = (getDisplays() ?? Enumerable.Empty<DisplayInfo>()).ToList(); }
            catch (Exception ex)
            {
                failures.Add("Monitorliste: " + ex.Message);
                return new LegacyProfileCleanupResult(0, failures);
            }
            foreach (var display in displays)
            {
                if (display == null) continue;
                try
                {
                    if (!hasRestorePoint(display)) continue;
                    reset(display);
                    restored++;
                }
                catch (Exception ex)
                {
                    failures.Add((string.IsNullOrWhiteSpace(display.FriendlyName) ? "Monitor" : display.FriendlyName)
                        + ": " + ex.Message);
                }
            }
            return new LegacyProfileCleanupResult(restored, failures);
        }
    }
}
