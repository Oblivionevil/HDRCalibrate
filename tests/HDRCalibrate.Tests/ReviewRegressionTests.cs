using HDRCalibrate.Dwm;
using HDRCalibrate.Services;
using HDRCalibrate.Setup.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace HDRCalibrate.Tests
{
    /// <summary>
    /// Regressions for the 1.7.4 and 1.7.5 review fixes. Only temp directories and
    /// in-memory fakes are used; no helper, DWM, registry or color profile.
    /// </summary>
    internal static class ReviewRegressionTests
    {
        public static void IpcRootAcceptsOnlyTheAppRequestDirectory()
        {
            var baseDirectory = Path.Combine(Path.GetTempPath(), "HDRCalibrate.IpcRoot-" + Guid.NewGuid().ToString("N"));
            var root = Path.Combine(baseDirectory, "OtherUser", "HDRCalibrate", "Requests");
            var link = Path.Combine(baseDirectory, "Link", "HDRCalibrate", "Requests");
            Directory.CreateDirectory(root);
            try
            {
                // Over-the-shoulder elevation: another account's directory is accepted
                // when it has exactly the shape the app creates.
                Check(DwmIpcRoot.Validate(root) == root, "A valid request directory was rejected.");
                // Same-account elevation passes this process's own directory, whatever its form.
                Check(DwmIpcRoot.Validate(DwmIpcRoot.GetDefault()) == DwmIpcRoot.GetDefault()
                    && DwmIpcRoot.Validate(DwmIpcRoot.GetDefault() + "\\") == DwmIpcRoot.GetDefault(),
                    "The helper rejected its own request directory.");
                Check(DwmIpcRoot.Validate(root + "\\") == root, "A trailing separator changed the request directory.");
                var wrongLeaf = Path.Combine(baseDirectory, "OtherUser", "HDRCalibrate", "Other");
                var wrongParent = Path.Combine(baseDirectory, "OtherUser", "Other", "Requests");
                Directory.CreateDirectory(wrongLeaf);
                Directory.CreateDirectory(wrongParent);
                foreach (var invalid in new[]
                {
                    null, string.Empty, @"HDRCalibrate\Requests", @"\\server\share\HDRCalibrate\Requests",
                    @"\\?\" + root, root.Replace('\\', '/'), root + "\"", wrongLeaf, wrongParent,
                    Path.Combine(baseDirectory, "OtherUser", "HDRCalibrate", "..", "HDRCalibrate", "Requests"),
                    Path.Combine(baseDirectory, "Missing", "HDRCalibrate", "Requests")
                })
                    Expect<UnauthorizedAccessException>(() => DwmIpcRoot.Validate(invalid), "Accepted IPC root: " + invalid);

                var target = Path.Combine(baseDirectory, "Target", "HDRCalibrate", "Requests");
                Directory.CreateDirectory(target);
                Directory.CreateDirectory(Path.GetDirectoryName(link));
                Check(CreateJunction(link, target), "The junction for the reparse-point case could not be created.");
                Expect<UnauthorizedAccessException>(() => DwmIpcRoot.Validate(link), "A junctioned request directory was followed.");
            }
            finally
            {
                if (Directory.Exists(link)) Directory.Delete(link, false);
                Directory.Delete(baseDirectory, true);
            }
        }

        public static void LegacyProfileCleanupNeverBlocks()
        {
            var restored = new List<DisplayInfo>();
            var first = Display("Monitor A");
            var broken = Display("Monitor B");
            var withoutState = Display("Monitor C");
            var result = LegacyProfileCleanup.Run(
                () => new[] { first, broken, withoutState },
                display => display != withoutState,
                display =>
                {
                    if (display == broken) throw new InvalidOperationException("Windows lehnt das Profil ab.");
                    restored.Add(display);
                });
            Check(result.Restored == 1 && restored.SequenceEqual(new[] { first }),
                "A failing display stopped the cleanup of the other displays.");
            Check(result.Failures.Count == 1 && result.Failures[0].Contains("Monitor B")
                && result.Failures[0].Contains("Windows lehnt das Profil ab."), "The failed display was not reported.");
            Check(result.Format().StartsWith("; 1 alte MHC2-Zuordnung bereinigt; Warnung:", StringComparison.Ordinal),
                "The cleanup warning is missing from the status text.");

            var unavailable = LegacyProfileCleanup.Run(
                () => { throw new IOException("Monitorabfrage fehlgeschlagen"); }, display => true, display => { });
            Check(unavailable.Restored == 0 && unavailable.Failures.Single().Contains("Monitorabfrage fehlgeschlagen"),
                "A failed display enumeration blocked instead of being reported.");
            Check(LegacyProfileCleanup.Run(() => new DisplayInfo[0], display => true, display => { }).Format() == string.Empty,
                "An empty cleanup produced status text.");
        }

        public static void AbandonedHelperHoldsRestartGuard()
        {
            var root = Path.Combine(Path.GetTempPath(), "HDRCalibrate.HelperHold-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var now = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
                var running = true;
                var service = new DwmPatchService(new DwmRestartCooldown(root, () => now), () => running);
                var abandon = typeof(DwmPatchService).GetMethod("MarkHelperAbandoned",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                abandon.Invoke(service, null);
                Check(service.GetRestartCooldownRemaining() == DwmRestartCooldown.Duration, "The timeout did not start the guard.");
                // The watchdog may restart DWM long after the app stopped waiting.
                now = now.AddSeconds(30);
                Check(service.GetRestartCooldownRemaining() == DwmRestartCooldown.Duration,
                    "The guard expired while the abandoned helper still ran.");
                running = false;
                Check(service.GetRestartCooldownRemaining() == DwmRestartCooldown.Duration,
                    "The guard did not count from the last time a helper ran.");
                now = now.AddSeconds(10);
                Check(service.GetRestartCooldownRemaining() == TimeSpan.Zero, "The guard never ended after the helper exited.");
                running = true;
                Check(service.GetRestartCooldownRemaining() == TimeSpan.Zero,
                    "An unrelated later helper was treated as the abandoned one.");

                // Hidden window: nobody polls while the abandoned helper runs. The
                // guard must count from when its exit is noticed, not from the timeout.
                abandon.Invoke(service, null);
                now = now.AddSeconds(40);
                running = false;
                Check(service.GetRestartCooldownRemaining() == DwmRestartCooldown.Duration,
                    "Without polling the guard had expired before the helper's exit was noticed.");
                now = now.AddSeconds(10);
                Check(service.GetRestartCooldownRemaining() == TimeSpan.Zero, "The noticed exit kept the guard forever.");

                // The exit is noticed while the marker cannot be written. The old
                // marker has expired; the guard must hold until the write succeeds.
                abandon.Invoke(service, null);
                now = now.AddSeconds(30);
                running = false;
                var enforce = typeof(DwmPatchService).GetMethod("EnforceAbandonedHelperHold",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                using (new FileStream(Path.Combine(root, "last-dwm-restart.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    Check(service.GetRestartCooldownRemaining() == DwmRestartCooldown.Duration,
                        "A failed marker write lifted the guard after the abandoned helper ended.");
                    Check(service.GetRestartCooldownRemaining() == DwmRestartCooldown.Duration,
                        "The guard was lifted on the next poll while the marker still could not be written.");
                    var rejected = false;
                    try { enforce.Invoke(service, null); }
                    catch (System.Reflection.TargetInvocationException ex) { rejected = ex.InnerException is DwmRestartCooldownException; }
                    Check(rejected, "A helper could start while the abandoned helper's end was not recorded.");
                }
                Check(service.GetRestartCooldownRemaining() == DwmRestartCooldown.Duration,
                    "The guard did not count from the recorded exit once the marker was writable again.");
                now = now.AddSeconds(10);
                Check(service.GetRestartCooldownRemaining() == TimeSpan.Zero, "The recorded exit kept the guard forever.");

                var mayRun = typeof(DwmPatchService).GetMethod("HelperMayStillRun",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                Func<bool, bool, bool, bool, bool> rule = (started, completed, timedOut, finalized) =>
                    (bool)mayRun.Invoke(null, new object[] { started, completed, timedOut, finalized });
                // A timed-out test supervisor is killed (completed) but its watchdog may still run.
                Check(rule(true, true, true, true), "A killed, timed-out supervisor was treated as finished.");
                Check(rule(true, false, false, true) && rule(true, true, false, false),
                    "A running helper or an unfinalized supervised outcome was treated as finished.");
                Check(!rule(true, true, false, true) && !rule(false, false, true, false),
                    "A finished or never started helper was treated as abandoned.");
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        public static void StateWithoutStartTimeIsNotCurrent()
        {
            var dwm = DwmPatchService.GetCurrentSessionDwmProcessId();
            Check(dwm > 0, "No DWM process in this session.");
            var state = new PersistedDwmState
            {
                WasApplied = true,
                DwmProcessId = dwm,
                DwmStartTimeUtcTicks = 0,
                SessionId = Process.GetCurrentProcess().SessionId
            };
            Check(!DwmPatchService.IsAppliedStateCurrent(state), "A state without start time matched the current DWM by PID.");
        }

        public static void DwmStateStoreIsHardened()
        {
            var root = Path.Combine(Path.GetTempPath(), "HDRCalibrate.StateStore-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new DwmStateStore(root);
                File.WriteAllText(store.StatePath,
                    "<?xml version=\"1.0\"?><!DOCTYPE PersistedDwmState [<!ENTITY x SYSTEM \"file:///C:/Windows/win.ini\">]>" +
                    "<PersistedDwmState><WasApplied>true</WasApplied><DwmCoreVersion>&x;</DwmCoreVersion></PersistedDwmState>");
                Expect<InvalidDataException>(() => store.Load(), "A DTD in the DWM state file was processed.");
                File.WriteAllText(store.StatePath + ".bak", "old");
                File.WriteAllText(store.StatePath + ".tmp", "partial");
                store.Clear();
                Check(Directory.GetFiles(root).Length == 0, "Clear left DWM state files behind.");
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        private static DisplayInfo Display(string name)
        {
            var display = new DisplayInfo();
            typeof(DisplayInfo).GetProperty(nameof(DisplayInfo.FriendlyName)).SetValue(display, name, null);
            return display;
        }

        public static void UnrecordedHelperEndHoldsRestartGuard()
        {
            var root = Path.Combine(Path.GetTempPath(), "HDRCalibrate.HelperEnd-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var now = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
                var cooldown = new DwmRestartCooldown(root, () => now);
                var service = new DwmPatchService(cooldown, () => false);
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var recordEnd = typeof(DwmPatchService).GetMethod("RecordHelperEnd", flags);
                var enforce = typeof(DwmPatchService).GetMethod("EnforceAbandonedHelperHold", flags);
                cooldown.EnforceAndMark();
                // UAC wait and helper run made the request marker older than the guard.
                now = now.AddSeconds(30);
                using (new FileStream(Path.Combine(root, "last-dwm-restart.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    recordEnd.Invoke(service, null);
                    Check(service.GetRestartCooldownRemaining() == DwmRestartCooldown.Duration,
                        "An unrecorded helper end lifted the restart guard.");
                    var rejected = false;
                    try { enforce.Invoke(service, null); }
                    catch (System.Reflection.TargetInvocationException ex) { rejected = ex.InnerException is DwmRestartCooldownException; }
                    Check(rejected, "A helper could start right after an unrecorded helper end.");
                }
                Check(service.GetRestartCooldownRemaining() == DwmRestartCooldown.Duration,
                    "The guard did not count from the recorded end.");
                now = now.AddSeconds(10);
                Check(service.GetRestartCooldownRemaining() == TimeSpan.Zero, "The recorded end kept the guard forever.");
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        public static void LegacyProfileResetKeepsLaterUserChoice()
        {
            var rule = typeof(ProfileService).GetMethod("KeepsLaterUserChoice",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Func<string, string, bool> keeps = (current, original) => (bool)rule.Invoke(null, new object[] { current, original });
            Check(keeps("Monitor-Kalibrierung.icm", "Original.icm"),
                "A profile the user chose later would be replaced by the old original.");
            Check(!keeps("HDRCalibrate_1_0_ABCDEF.icm", "Original.icm") && !keeps("original.ICM", "Original.icm")
                && !keeps(null, "Original.icm"),
                "HDR Calibrate's own, the original or a missing default profile was treated as a later choice.");
        }

        public static void LegacyRestorePointSurvivesANewAdapterLuid()
        {
            var directory = Path.Combine(Path.GetTempPath(), "HDRCalibrate.LegacyLuid-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var store = new ProfileStateStore(directory);
                store.Upsert(new PersistedDisplayState
                {
                    DisplayKey = "00000000000A1B2C-0-1",
                    OriginalProfileName = "Original.icm",
                    LastAppliedProfileName = "HDRCalibrate_00000000000A1B2C_0_ABCDEF012345.icm",
                    Settings = new HDRCalibrate.Core.CalibrationSettings()
                });
                var service = new ProfileService(store);
                Func<string, DisplayInfo> display = active =>
                {
                    var item = new DisplayInfo();
                    typeof(DisplayInfo).GetProperty(nameof(DisplayInfo.AdapterId)).SetValue(item,
                        new HDRCalibrate.Native.Luid { LowPart = 0xD0E1, HighPart = 0 }, null);
                    typeof(DisplayInfo).GetProperty(nameof(DisplayInfo.ActiveProfileName)).SetValue(item, active, null);
                    return item;
                };
                var rebooted = display("HDRCalibrate_00000000000A1B2C_0_ABCDEF012345.icm");
                Check(service.GetState(rebooted)?.OriginalProfileName == "Original.icm" && service.HasLegacyProfile(rebooted),
                    "After a reboot (new adapter LUID) the active legacy profile lost its restore point.");
                Check(service.GetState(display("Original.icm")) == null && !service.HasLegacyProfile(display("Original.icm")),
                    "A foreign active profile was matched to a legacy restore point.");
                var orphan = display("HDRCalibrate_OTHER_000000000000.icm");
                Check(service.GetState(orphan) == null && service.HasLegacyProfile(orphan),
                    "An active own profile without restore point was neither matched exactly nor reported.");

                File.WriteAllText(Path.Combine(directory, "state.xml"),
                    "<?xml version=\"1.0\"?><!DOCTYPE a [<!ENTITY x \"y\">]><PersistedAppState>&x;</PersistedAppState>");
                Expect<InvalidDataException>(() => store.Load(), "The legacy state accepted a DTD.");
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        public static void SetupNeverWritesThroughNestedJunctions()
        {
            var baseDirectory = Path.Combine(Path.GetTempPath(), "HDRCalibrate.SetupJunction-" + Guid.NewGuid().ToString("N"));
            var install = Path.Combine(baseDirectory, "Install");
            var outside = Path.Combine(baseDirectory, "Outside");
            var link = Path.Combine(install, "Native");
            var victim = Path.Combine(outside, "1.7.5", "HDRCalibrate.SelectiveDwmHook.dll");
            try
            {
                Directory.CreateDirectory(install);
                Directory.CreateDirectory(Path.GetDirectoryName(victim));
                File.WriteAllText(victim, "outside");
                var source = Path.Combine(baseDirectory, "new.dll");
                File.WriteAllText(source, "new");
                Check(CreateJunction(link, outside), "The junction below the install directory could not be created.");
                var relative = @"Native\1.7.5\HDRCalibrate.SelectiveDwmHook.dll";
                var transaction = new FileTransaction(install, null);
                Expect<IOException>(() => transaction.Put(source, relative), "Setup wrote through a junction below its root.");
                Check(!transaction.TryRemove(relative), "Setup moved a file reached through a junction.");
                Check(transaction.Rollback(), "The rejected transaction could not be rolled back.");
                Check(File.ReadAllText(victim) == "outside", "A file outside the install directory was changed.");
            }
            finally
            {
                if (Directory.Exists(link)) Directory.Delete(link, false);
                Directory.Delete(baseDirectory, true);
            }
        }

        public static void SetupKeepsBackupsOfAnIncompleteRollback()
        {
            var root = Path.Combine(Path.GetTempPath(), "HDRCalibrate.SetupBackup-" + Guid.NewGuid().ToString("N"));
            try
            {
                var target = Path.Combine(root, "HDRCalibrate.exe");
                Directory.CreateDirectory(root);
                File.WriteAllText(target, "old");
                var source = Path.Combine(Path.GetTempPath(), "HDRCalibrate.SetupBackupSource-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(source, "new");
                var transaction = new FileTransaction(root, null);
                transaction.Put(source, "HDRCalibrate.exe");
                bool restored;
                // The new file stays locked, so the previous version cannot return.
                using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None))
                    restored = transaction.Rollback();
                File.Delete(source);
                Check(!restored, "The locked rollback was reported as complete.");
                Func<string[]> backups = () => Directory.GetDirectories(root, ProductInfo.BackupDirectoryPrefix + "*");
                Check(backups().Length == 1 && File.ReadAllText(Path.Combine(backups()[0], "0000-HDRCalibrate.exe")) == "old"
                    && File.Exists(Path.Combine(backups()[0], FileTransaction.PendingMarkerFileName)),
                    "The only previous copy was not kept as a pending backup.");

                // A leftover of a committed transaction is disposable.
                var disposable = Path.Combine(root, ProductInfo.BackupDirectoryPrefix + new string('a', 32));
                Directory.CreateDirectory(disposable);
                File.WriteAllText(Path.Combine(disposable, "0000-old.dll"), "locked earlier");
                FileTransaction.DeleteStaleBackups(root, null);
                Check(!Directory.Exists(disposable), "A committed leftover backup was kept.");
                Check(backups().Length == 1, "The next setup run deleted the only copy of an incomplete rollback.");
                FileTransaction.DeleteStaleBackups(root, null, true);
                Check(backups().Length == 0, "A superseded pending backup was kept after a later commit.");
            }
            finally
            {
                foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(root, true);
            }
        }

        public static void ProgramGammaReportsThatNoBuildIsReleased()
        {
            Check(!ProgramGammaBuildSupport.HasReachableBuild,
                "A build is listed for both the native hook and the global correction; update the message.");
            Check(ProgramGammaBuildSupport.GetSystemUnavailableReason() == ProgramGammaBuildSupport.NoReleasedBuildMessage,
                "Program gamma did not report that no Windows build is released for it.");
        }

        private static bool CreateJunction(string link, string target)
        {
            using (var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                Arguments = "/d /c mklink /J \"" + link + "\" \"" + target + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }))
            {
                process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                process.WaitForExit(10000);
                return process.HasExited && process.ExitCode == 0 && Directory.Exists(link)
                    && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0;
            }
        }

        private static void Expect<T>(Action action, string message) where T : Exception
        {
            try { action(); }
            catch (T) { return; }
            throw new InvalidOperationException(message);
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
