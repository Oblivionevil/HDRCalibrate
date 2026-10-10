using HDRCalibrate.Services;
using System;
using System.Globalization;
using System.IO;

namespace HDRCalibrate.Tests
{
    internal static class DwmRestartCooldownTests
    {
        private static readonly DateTime AttemptUtc = new DateTime(
            2026, 9, 22, 16, 52, 51, DateTimeKind.Utc).AddTicks(1546453);

        public static void BoundaryAndNoWriteOnReject()
        {
            WithTemporaryRoot(root =>
            {
                var marker = Path.Combine(root, "last-dwm-restart.txt");
                var now = AttemptUtc;
                var cooldown = new DwmRestartCooldown(root, () => now);
                cooldown.EnforceAndMark();
                var original = File.ReadAllText(marker);
                var originalWriteUtc = File.GetLastWriteTimeUtc(marker);

                // Reproduce the user's attempt: 96 ms short of the unchanged
                // ten-second guard, immediately after a successful timed test.
                now = AttemptUtc.AddTicks(99042353);
                var expected = TimeSpan.FromTicks(957647);
                Check(cooldown.GetRemaining() == expected, "The 9.9042353-second boundary was lost.");
                var rejected = Expect<DwmRestartCooldownException>(() => cooldown.EnforceAndMark());
                Check(rejected.Remaining == expected, "The typed cooldown error lost its exact remaining time.");
                Check(File.ReadAllText(marker) == original, "Rejected Apply rewrote the restart marker.");
                Check(File.GetLastWriteTimeUtc(marker) == originalWriteUtc, "Rejected Apply extended the guard.");

                now = AttemptUtc.AddSeconds(10).AddTicks(-1);
                Check(cooldown.GetRemaining() == TimeSpan.FromTicks(1), "The last protected tick was allowed.");
                Expect<DwmRestartCooldownException>(() => cooldown.EnforceAndMark());
                now = AttemptUtc.AddSeconds(10);
                Check(cooldown.GetRemaining() == TimeSpan.Zero, "Exactly ten seconds must be eligible.");
                cooldown.EnforceAndMark();
                Check(File.ReadAllText(marker) == now.Ticks.ToString(CultureInfo.InvariantCulture),
                    "An eligible restart did not mark the new attempt.");
                Check(cooldown.GetRemaining() == DwmRestartCooldown.Duration, "The new attempt has no guard.");

                now = now.AddMinutes(1);
                Check(cooldown.GetRemaining() == TimeSpan.Zero, "Expired cooldown became negative or stayed blocked.");
            });
        }

        public static void InvalidAndFutureMarkers()
        {
            WithTemporaryRoot(root =>
            {
                var marker = Path.Combine(root, "last-dwm-restart.txt");
                var now = AttemptUtc.AddSeconds(3);
                var monotonic = TimeSpan.FromHours(1);
                var cooldown = new DwmRestartCooldown(root, () => now, () => monotonic);
                foreach (var invalid in new[]
                {
                    string.Empty, "broken", "0", "-1", long.MaxValue.ToString(CultureInfo.InvariantCulture),
                    DateTime.MaxValue.Ticks.ToString(CultureInfo.InvariantCulture), new string('9', 256)
                })
                {
                    File.WriteAllText(marker, invalid);
                    File.SetLastWriteTimeUtc(marker, AttemptUtc);
                    var expected = DwmRestartCooldown.Duration - (now - File.GetLastWriteTimeUtc(marker));
                    Check(cooldown.GetRemaining() == expected && expected > TimeSpan.Zero,
                        "A malformed recent marker bypassed its modification-time guard.");
                    Expect<DwmRestartCooldownException>(() => cooldown.EnforceAndMark());
                    Check(File.ReadAllText(marker) == invalid, "A malformed blocked marker was overwritten.");
                }

                // A backwards clock change must neither bypass the guard nor
                // stretch it until the wall clock reaches the marker again.
                var future = now.AddHours(2);
                File.WriteAllText(marker, future.Ticks.ToString(CultureInfo.InvariantCulture));
                Check(cooldown.GetRemaining() == DwmRestartCooldown.Duration,
                    "A future timestamp bypassed the guard or was not bounded after a backwards clock change.");
                Expect<DwmRestartCooldownException>(() => cooldown.EnforceAndMark());
                Check(File.ReadAllText(marker) == future.Ticks.ToString(CultureInfo.InvariantCulture),
                    "A future marker was silently reset.");
                monotonic += TimeSpan.FromSeconds(6);
                now = now.AddSeconds(6);
                Check(cooldown.GetRemaining() == TimeSpan.FromSeconds(4),
                    "A future marker did not count down monotonically.");
                now = now.AddHours(-1);
                Check(cooldown.GetRemaining() == TimeSpan.FromSeconds(4),
                    "A second backwards clock change restarted or bypassed the bounded guard.");
                monotonic += TimeSpan.FromSeconds(4).Subtract(TimeSpan.FromTicks(1));
                Check(cooldown.GetRemaining() == TimeSpan.FromTicks(1), "The last protected tick was allowed.");
                Expect<DwmRestartCooldownException>(() => cooldown.EnforceAndMark());
                monotonic += TimeSpan.FromTicks(1);
                Check(cooldown.GetRemaining() == TimeSpan.Zero, "Future marker never became eligible.");
                Check(File.ReadAllText(marker) == future.Ticks.ToString(CultureInfo.InvariantCulture),
                    "Reading a future marker changed it.");

                File.WriteAllText(marker, "malformed");
                File.SetLastWriteTimeUtc(marker, now.AddMinutes(1));
                Check(cooldown.GetRemaining() == DwmRestartCooldown.Duration,
                    "A future modification time was treated as an expired malformed marker.");
                Expect<DwmRestartCooldownException>(() => cooldown.EnforceAndMark());
                monotonic += DwmRestartCooldown.Duration;
                cooldown.EnforceAndMark();
                Check(File.ReadAllText(marker) == now.Ticks.ToString(CultureInfo.InvariantCulture),
                    "An expired future marker was not replaced by the current attempt.");
                Check(cooldown.GetRemaining() == DwmRestartCooldown.Duration, "The new attempt has no guard.");
            });
        }

        public static void IndependentStateAndReadOnlyQuery()
        {
            WithTemporaryRoot(root =>
            {
                var missingRoot = Path.Combine(root, "not-created-by-query");
                var now = AttemptUtc;
                var missing = new DwmRestartCooldown(missingRoot, () => now);
                var service = new DwmPatchService(missing);
                Check(service.GetRestartCooldownRemaining() == TimeSpan.Zero, "Missing state must have no guard.");
                Check(!Directory.Exists(missingRoot), "Reading the UI countdown created a state directory.");

                var cooldown = new DwmRestartCooldown(root, () => now);
                service = new DwmPatchService(cooldown);
                cooldown.EnforceAndMark();
                var marker = Path.Combine(root, "last-dwm-restart.txt");
                var original = File.ReadAllText(marker);
                var originalWriteUtc = File.GetLastWriteTimeUtc(marker);
                now = now.AddSeconds(6);
                for (var repeat = 0; repeat < 5; repeat++)
                    Check(service.GetRestartCooldownRemaining() == TimeSpan.FromSeconds(4),
                        "UI and enforcement do not share the same clock and marker.");
                var secondInstance = new DwmRestartCooldown(root, () => now);
                Expect<DwmRestartCooldownException>(() => secondInstance.EnforceAndMark());
                Check(File.ReadAllText(marker) == original && File.GetLastWriteTimeUtc(marker) == originalWriteUtc,
                    "Repeated countdown reads or a second instance changed the marker.");
                Check(missing.GetRemaining() == TimeSpan.Zero, "The injected state roots leaked into each other.");
            });
        }

        public static void UnusedMarkIsReleasedOnlyForItsOwnAttempt()
        {
            WithTemporaryRoot(root =>
            {
                var marker = Path.Combine(root, "last-dwm-restart.txt");
                var now = AttemptUtc;
                var cooldown = new DwmRestartCooldown(root, () => now);

                // A declined UAC prompt never restarted DWM: its marker is released.
                var declined = cooldown.EnforceAndMark();
                Check(cooldown.GetRemaining() == DwmRestartCooldown.Duration, "The attempt was not guarded.");
                cooldown.RevertUnusedMark(declined);
                Check(!File.Exists(marker) && cooldown.GetRemaining() == TimeSpan.Zero,
                    "A declined start kept blocking the next attempt.");
                cooldown.RevertUnusedMark(declined);

                // A stale release must never remove a newer, real attempt.
                now = now.AddSeconds(1);
                var started = cooldown.EnforceAndMark();
                cooldown.RevertUnusedMark(declined);
                Check(File.ReadAllText(marker) == started.ToString(CultureInfo.InvariantCulture)
                    && cooldown.GetRemaining() == DwmRestartCooldown.Duration,
                    "Releasing an older attempt removed the guard of a started helper.");

                File.WriteAllText(marker, "malformed");
                cooldown.RevertUnusedMark(started);
                Check(File.ReadAllText(marker) == "malformed", "A foreign marker was removed.");
            });
        }

        public static void GuardErrorsFailClosed()
        {
            WithTemporaryRoot(root =>
            {
                var marker = Path.Combine(root, "last-dwm-restart.txt");
                var original = AttemptUtc.Ticks.ToString(CultureInfo.InvariantCulture);
                File.WriteAllText(marker, original);
                var cooldown = new DwmRestartCooldown(root, () => AttemptUtc.AddSeconds(30));
                using (var lease = new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    Expect<IOException>(() => cooldown.GetRemaining());
                    Expect<IOException>(() => cooldown.EnforceAndMark());
                }
                Check(File.ReadAllText(marker) == original, "An unreadable marker was overwritten.");

                var wrongClock = new DwmRestartCooldown(root, () => DateTime.SpecifyKind(AttemptUtc, DateTimeKind.Unspecified));
                Expect<InvalidDataException>(() => wrongClock.GetRemaining());
                Expect<InvalidDataException>(() => wrongClock.EnforceAndMark());
                var failedClock = new DwmRestartCooldown(root, () => { throw new IOException("Test clock unavailable."); });
                Expect<IOException>(() => failedClock.GetRemaining());
                Expect<IOException>(() => failedClock.EnforceAndMark());
                Check(File.ReadAllText(marker) == original, "Unavailable clock data removed the guard.");
                Expect<ArgumentOutOfRangeException>(() => new DwmRestartCooldownException(TimeSpan.Zero));
                Expect<ArgumentOutOfRangeException>(() => new DwmRestartCooldownException(TimeSpan.FromSeconds(-1)));
            });
        }

        private static void WithTemporaryRoot(Action<string> test)
        {
            // No LocalApplicationData state, helper, registry, DWM or UI is used.
            var root = Path.Combine(Path.GetTempPath(), "HDRCalibrate.CooldownTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try { test(root); }
            finally
            {
                File.Delete(Path.Combine(root, "last-dwm-restart.txt"));
                Directory.Delete(root, false);
            }
        }

        private static T Expect<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T error) { return error; }
            throw new InvalidOperationException("Expected " + typeof(T).Name + ".");
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
