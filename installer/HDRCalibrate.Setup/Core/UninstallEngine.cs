using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace HDRCalibrate.Setup.Core
{
    public sealed class UninstallResult
    {
        public UninstallResult(IReadOnlyList<string> retainedNativeDirectories, string remainingDirectory)
        {
            RetainedNativeDirectories = retainedNativeDirectories;
            RemainingDirectory = remainingDirectory;
        }

        public IReadOnlyList<string> RetainedNativeDirectories { get; }
        /// <summary>The installation directory if it was not removed; otherwise null.</summary>
        public string RemainingDirectory { get; }
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

            var retained = Directory.Exists(directory) && !SetupPaths.IsReparsePoint(directory)
                ? NativeFiles.RemoveAll(directory, log)
                : (IReadOnlyList<string>)new string[0];

            var record = Directory.Exists(directory) ? InstallationRecord.Read(directory, log) : null;
            if (Directory.Exists(directory) && !SetupPaths.IsReparsePoint(directory))
            {
                foreach (var relative in OwnedFiles(directory, record, manifest))
                    NativeFiles.TryDelete(SetupPaths.Combine(directory, relative), log);
                FileTransaction.DeleteStaleBackups(directory, log);
            }

            var locations = _context.Locations;
            NativeFiles.TryDelete(locations.StartMenuShortcut, log);
            NativeFiles.TryRemoveEmptyDirectory(locations.StartMenuDirectory);
            NativeFiles.TryDelete(locations.DesktopShortcut, log);

            registry.DeleteKey(ProductInfo.UninstallKey);
            registry.DeleteKey(ProductInfo.InnoUninstallKey);

            string remaining = null;
            if (Directory.Exists(directory))
            {
                var ownsDirectory = record?.CreatedDirectory
                    ?? InstallationLocator.SamePath(directory, locations.DefaultInstallDirectory);
                if (ownsDirectory) NativeFiles.TryRemoveEmptyDirectory(directory);
                if (Directory.Exists(directory)) remaining = directory;
            }
            log.Write("Deinstallation abgeschlossen." + (remaining != null ? " Ordner bleibt: " + remaining : string.Empty));
            return new UninstallResult(retained, remaining);
        }

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
