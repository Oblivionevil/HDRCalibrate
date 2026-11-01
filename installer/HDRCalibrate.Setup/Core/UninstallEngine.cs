using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace HDRCalibrate.Setup.Core
{
    public sealed class UninstallResult
    {
        public UninstallResult(IReadOnlyList<string> retainedNativeDirectories, string remainingDirectory,
            IReadOnlyList<string> failedFiles = null, bool foreignEntriesRemain = false)
        {
            RetainedNativeDirectories = retainedNativeDirectories;
            RemainingDirectory = remainingDirectory;
            FailedFiles = failedFiles ?? new string[0];
            ForeignEntriesRemain = foreignEntriesRemain;
        }

        public IReadOnlyList<string> RetainedNativeDirectories { get; }
        /// <summary>The installation directory if it was not removed; otherwise null.</summary>
        public string RemainingDirectory { get; }
        /// <summary>
        /// Owned application files that could not be deleted. While any remain,
        /// the uninstaller, its record and the Apps entry are kept for a retry.
        /// </summary>
        public IReadOnlyList<string> FailedFiles { get; }
        public bool IsComplete => FailedFiles.Count == 0;
        /// <summary>True when the kept directory holds entries Setup did not install.</summary>
        public bool ForeignEntriesRemain { get; }
    }

    public sealed class UninstallEngine
    {
        private readonly SetupContext _context;

        public UninstallEngine(SetupContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        /// <param name="manifest">This setup's own payload list; used when no record exists (former Inno install).</param>
        public UninstallResult Uninstall(PayloadManifest manifest, IProgress<string> progress = null)
        {
            var text = _context.Text;
            var log = _context.Log;
            var existing = InstallationLocator.Find(_context);
            if (!existing.IsInstalled) throw new SetupException(text.NotInstalled);
            var directory = existing.Directory;
            if (InstallationLocator.Normalize(directory) == null)
                throw new SetupException(text.InstallDirectoryInvalid);
            log.Write("Deinstallation aus " + directory);

            var appExecutable = ProductInfo.AppExecutable(directory);
            if (File.Exists(appExecutable))
            {
                progress?.Report(text.ProgressStopping);
                var code = _context.Stopper.Stop(appExecutable, appExecutable);
                log.Write("Beenden der laufenden Instanz: Exitcode " + code.ToString(CultureInfo.InvariantCulture));
                if (code != 0) throw new SetupException(InstallationLocator.StopMessage(text, code));
            }

            progress?.Report(text.ProgressRemoving);
            var registry = _context.Registry;
            // The app may have enabled startup itself; remove only this installation's command.
            if (ProductInfo.IsOwnRunCommand(registry.GetString(ProductInfo.RunKey, ProductInfo.RunValueName), directory))
                registry.DeleteValue(ProductInfo.RunKey, ProductInfo.RunValueName);

            var usable = Directory.Exists(directory) && !SetupPaths.IsReparsePoint(directory);
            var retained = usable ? NativeFiles.RemoveAll(directory, log) : (IReadOnlyList<string>)new string[0];

            var record = Directory.Exists(directory) ? InstallationRecord.Read(directory, log) : null;
            var failed = new List<string>();
            if (usable)
            {
                // The uninstaller and its record go last: while another owned file
                // stays, both stay with the Apps entry so the uninstall can be repeated.
                var owned = OwnedFiles(directory, record, manifest).ToList();
                foreach (var relative in owned.Where(file => !IsUninstallerFile(file)))
                    if (!NativeFiles.TryDelete(SetupPaths.Combine(directory, relative), log)) failed.Add(relative);
                if (failed.Count == 0)
                    foreach (var relative in owned.Where(IsUninstallerFile))
                        if (!TryDeleteUninstallerFile(SetupPaths.Combine(directory, relative), log)) failed.Add(relative);
                FileTransaction.DeleteStaleBackups(directory, log);
            }

            var locations = _context.Locations;
            NativeFiles.TryDelete(locations.StartMenuShortcut, log);
            NativeFiles.TryRemoveEmptyDirectory(locations.StartMenuDirectory);
            NativeFiles.TryDelete(locations.DesktopShortcut, log);

            if (failed.Count == 0)
            {
                registry.DeleteKey(ProductInfo.UninstallKey);
                registry.DeleteKey(ProductInfo.InnoUninstallKey);
            }
            else
            {
                log.Write("Deinstallation unvollständig; Eintrag unter Apps bleibt: " + string.Join(", ", failed));
            }

            string remaining = null;
            var foreign = false;
            if (Directory.Exists(directory))
            {
                var ownsDirectory = record?.CreatedDirectory
                    ?? InstallationLocator.SamePath(directory, locations.DefaultInstallDirectory);
                if (ownsDirectory) NativeFiles.TryRemoveEmptyDirectory(directory);
                if (Directory.Exists(directory))
                {
                    remaining = directory;
                    var expected = new HashSet<string>(failed.Select(file => file.Split('\\')[0]), StringComparer.OrdinalIgnoreCase);
                    if (failed.Count != 0)
                    {
                        expected.Add(ProductInfo.SetupExeName);
                        expected.Add(ProductInfo.RecordFileName);
                    }
                    if (retained.Count != 0) expected.Add(ProductInfo.NativeDirectoryName);
                    try
                    {
                        foreign = Directory.EnumerateFileSystemEntries(directory)
                            .Any(entry => !expected.Contains(Path.GetFileName(entry)));
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { foreign = true; }
                }
            }
            log.Write("Deinstallation abgeschlossen." + (remaining != null ? " Ordner bleibt: " + remaining : string.Empty));
            return new UninstallResult(retained, remaining, failed, foreign);
        }

        private static bool TryDeleteUninstallerFile(string path, ISetupLog log)
        {
            // The registered uninstaller relaunches from a temporary copy; the
            // original process may need a moment to exit and release its image.
            for (var attempt = 0; ; attempt++)
            {
                if (NativeFiles.TryDelete(path, log)) return true;
                if (attempt >= 10 || SetupPaths.IsReparsePoint(path)) return false;
                System.Threading.Thread.Sleep(300);
            }
        }

        private static bool IsUninstallerFile(string relativePath) =>
            string.Equals(relativePath, ProductInfo.SetupExeName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(relativePath, ProductInfo.RecordFileName, StringComparison.OrdinalIgnoreCase);

        private static IEnumerable<string> OwnedFiles(string directory, InstallationRecord record, PayloadManifest manifest)
        {
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (record != null) files.UnionWith(record.Files);
            if (manifest != null) files.UnionWith(manifest.Entries.Where(entry => !entry.IsNative).Select(entry => entry.RelativePath));
            files.Add(ProductInfo.SetupExeName);
            files.Add(ProductInfo.RecordFileName);
            files.UnionWith(ProductInfo.LegacyFiles);
            foreach (var file in Directory.GetFiles(directory))
            {
                var name = Path.GetFileName(file);
                if (ProductInfo.IsInnoUninstallerFile(name)) files.Add(name);
            }
            return files;
        }
    }
}
