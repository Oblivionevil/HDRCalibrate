using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace HDRCalibrate.Services
{
    public sealed class DwmRestartCooldownException : InvalidOperationException
    {
        public DwmRestartCooldownException(TimeSpan remaining)
            : base("Zum Schutz der Anzeige ist der nächste DWM-Neustart erst in " +
                Math.Ceiling(remaining.TotalSeconds).ToString(CultureInfo.CurrentCulture) +
                " Sekunden möglich.")
        {
            if (remaining <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(remaining));
            Remaining = remaining;
        }

        public TimeSpan Remaining { get; }
    }

    /// <summary>
    /// Shares the restart guard between the UI countdown and the final check
    /// immediately before starting a helper. Reading the countdown never changes
    /// the marker, and a rejected attempt never extends or removes the guard.
    /// </summary>
    public sealed class DwmRestartCooldown
    {
        public static readonly TimeSpan Duration = TimeSpan.FromSeconds(10);
        private static readonly object Gate = new object();
        private static readonly Stopwatch Monotonic = Stopwatch.StartNew();
        private readonly string _stateRoot;
        private readonly string _markerPath;
        private readonly Func<DateTime> _utcNow;
        private readonly Func<TimeSpan> _monotonicNow;
        private long? _futureMarkerTicks;
        private TimeSpan _futureMarkerObservedAt;

        public DwmRestartCooldown(string stateRoot = null, Func<DateTime> utcNow = null,
            Func<TimeSpan> monotonicNow = null)
        {
            _stateRoot = stateRoot ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HDRCalibrate");
            _markerPath = Path.Combine(_stateRoot, "last-dwm-restart.txt");
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _monotonicNow = monotonicNow ?? (() => Monotonic.Elapsed);
        }

        public TimeSpan GetRemaining()
        {
            lock (Gate)
                return GetRemaining(ReadUtcNow());
        }

        /// <summary>Returns the written marker value for <see cref="RevertUnusedMark"/>.</summary>
        public long EnforceAndMark()
        {
            lock (Gate)
            {
                var now = ReadUtcNow();
                var remaining = GetRemaining(now);
                if (remaining > TimeSpan.Zero)
                    throw new DwmRestartCooldownException(remaining);

                Directory.CreateDirectory(_stateRoot);
                File.WriteAllText(_markerPath, now.Ticks.ToString(CultureInfo.InvariantCulture));
                return now.Ticks;
            }
        }

        /// <summary>
        /// Releases a marker whose helper was never started (e.g. a declined UAC
        /// prompt), so no DWM restart happened. A marker was only written after the
        /// previous guard had expired, so removing it restores an equivalent state.
        /// Any other (newer or foreign) marker is left untouched.
        /// </summary>
        public void RevertUnusedMark(long markTicks)
        {
            lock (Gate)
            {
                string text;
                try { text = File.ReadAllText(_markerPath); }
                catch (FileNotFoundException) { return; }
                catch (DirectoryNotFoundException) { return; }
                if (!string.Equals(text, markTicks.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                    return;
                File.Delete(_markerPath);
                _futureMarkerTicks = null;
            }
        }

        private DateTime ReadUtcNow()
        {
            var now = _utcNow();
            if (now.Kind != DateTimeKind.Utc)
                throw new InvalidDataException("Die Uhrzeit für die DWM-Neustartsperre ist nicht in UTC verfügbar.");
            return now;
        }

        private TimeSpan GetRemaining(DateTime now)
        {
            // Only a genuinely missing marker means no previous attempt. Access
            // failures and sharing violations must not silently permit a restart.
            FileStream marker;
            try
            {
                marker = new FileStream(_markerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            catch (FileNotFoundException) { return TimeSpan.Zero; }
            catch (DirectoryNotFoundException) { return TimeSpan.Zero; }

            using (marker)
            {
                string text = null;
                if (marker.Length <= 128)
                {
                    using (var reader = new StreamReader(marker, System.Text.Encoding.UTF8, true, 128, true))
                        text = reader.ReadToEnd();
                }

                DateTime lastAttempt;
                if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
                    || ticks <= DateTime.MinValue.Ticks || ticks >= DateTime.MaxValue.Ticks)
                {
                    // Preserve the conservative fallback for malformed markers.
                    // The open read lease prevents replacement before the mtime read.
                    lastAttempt = File.GetLastWriteTimeUtc(_markerPath);
                }
                else
                {
                    lastAttempt = new DateTime(ticks, DateTimeKind.Utc);
                }

                if (lastAttempt > now)
                    return GetFutureMarkerRemaining(lastAttempt.Ticks);
                _futureMarkerTicks = null;
                var remaining = Duration - (now - lastAttempt);
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }

        private TimeSpan GetFutureMarkerRemaining(long markerTicks)
        {
            // After the wall clock moved backwards the real age of the marker is
            // unknown. It is never treated as expired, but it guards for exactly
            // one full Duration from its first observation, measured monotonically,
            // instead of until the wall clock catches up (possibly hours later).
            var observed = _monotonicNow();
            if (_futureMarkerTicks != markerTicks || observed < _futureMarkerObservedAt)
            {
                _futureMarkerTicks = markerTicks;
                _futureMarkerObservedAt = observed;
            }
            var remaining = Duration - (observed - _futureMarkerObservedAt);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }
}
