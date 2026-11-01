using HDRCalibrate.Dwm;
using HDRCalibrate.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace HDRCalibrate.Tests
{
    /// <summary>
    /// Regressions for the 1.7.4 review fixes. Only temp directories and
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

        private static DisplayInfo Display(string name)
        {
            var display = new DisplayInfo();
            typeof(DisplayInfo).GetProperty(nameof(DisplayInfo.FriendlyName)).SetValue(display, name, null);
            return display;
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
