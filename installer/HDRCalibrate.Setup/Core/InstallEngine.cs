using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace HDRCalibrate.Setup.Core
{
    public sealed class InstallOptions
    {
        public bool DesktopShortcut { get; set; }
        public bool Autostart { get; set; }
    }

    public sealed class InstallResult
    {
        public InstallResult(string installDirectory)
        {
            InstallDirectory = installDirectory;
        }

        public string InstallDirectory { get; }
        public string AppExecutable => ProductInfo.AppExecutable(InstallDirectory);
    }

    public sealed class InstallEngine
    {
        private readonly SetupContext _context;

        public InstallEngine(SetupContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public InstallResult Install(PayloadPackage package, string setupExecutable, InstallOptions options,
            IProgress<string> progress = null)
        {
            if (package == null) throw new SetupException(_context.Text.PayloadMissing);
            if (options == null) throw new ArgumentNullException(nameof(options));
            var text = _context.Text;
            var log = _context.Log;
            var existing = InstallationLocator.Find(_context);
            var directory = existing.Directory;
            if (InstallationLocator.Normalize(directory) == null)
                throw new SetupException(text.InstallDirectoryInvalid);
            log.Write("Installation " + package.Manifest.Version + " nach " + directory
                + (existing.IsInstalled ? " (vorhanden: " + (existing.Version ?? "unbekannt") + ")" : string.Empty));

            progress?.Report(text.ProgressVerifying);
            // A same-version native hook may be pinned in DWM. It is never replaced;
            // it must be exactly the packaged file, checked before any change.
            foreach (var hook in package.Manifest.Entries.Where(entry => entry.IsNativeHook))
            {
                var installed = SetupPaths.Combine(directory, hook.RelativePath);
                if (!File.Exists(installed)) continue;
                string hash;
                try { hash = SetupPaths.Sha256Of(installed); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { hash = null; }
                if (!string.Equals(hash, hook.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new SetupException(text.NativeVersionMismatch);
            }

            var staging = Path.Combine(_context.Locations.TempDirectory,
                ProductInfo.TempDirectoryPrefix + Guid.NewGuid().ToString("N"));
            try
            {
                var payloadDirectory = Path.Combine(staging, "payload");
                try { package.ExtractTo(payloadDirectory); }
                catch (InvalidDataException ex)
                {
                    log.Write("Paket ungültig: " + ex.Message);
                    throw new SetupException(text.PayloadMissing, false, ex);
                }

                var appExecutable = ProductInfo.AppExecutable(directory);
                if (File.Exists(appExecutable))
                {
                    progress?.Report(text.ProgressStopping);
                    // The new binary asks the installed instance to exit; names of the
                    // single-instance signals do not depend on the install path.
                    var code = _context.Stopper.Stop(Path.Combine(payloadDirectory, ProductInfo.AppExeName), appExecutable);
                    log.Write("Beenden der laufenden Instanz: Exitcode " + code.ToString(CultureInfo.InvariantCulture));
                    if (code != 0) throw new SetupException(InstallationLocator.StopMessage(text, code));
                }

                progress?.Report(text.ProgressCopying);
                var createdDirectory = !Directory.Exists(directory);
                Directory.CreateDirectory(directory);
                if (SetupPaths.IsReparsePoint(directory)) throw new SetupException(text.InstallDirectoryInvalid);
                FileTransaction.DeleteStaleBackups(directory, log);
                var previous = InstallationRecord.Read(directory, log);
                var transaction = new FileTransaction(directory, log);
                try
                {
                    foreach (var entry in package.Manifest.Entries)
                    {
                        if (entry.IsNativeHook && File.Exists(SetupPaths.Combine(directory, entry.RelativePath)))
                            continue;
                        transaction.Put(SetupPaths.Combine(payloadDirectory, entry.RelativePath), entry.RelativePath);
                    }
                    var installedSetup = Path.Combine(directory, ProductInfo.SetupExeName);
                    if (!InstallationLocator.SamePath(setupExecutable, installedSetup))
                        transaction.Put(setupExecutable, ProductInfo.SetupExeName);

                    var files = new List<string>(previous?.Files ?? Enumerable.Empty<string>());
                    files.AddRange(package.Manifest.Entries.Where(entry => !entry.IsNative).Select(entry => entry.RelativePath));
                    files.Add(ProductInfo.SetupExeName);
                    files.Add(ProductInfo.RecordFileName);
                    var ownsDirectory = createdDirectory || previous?.CreatedDirectory == true
                        || (previous == null && InstallationLocator.SamePath(directory, _context.Locations.DefaultInstallDirectory));
                    var recordFile = Path.Combine(staging, ProductInfo.RecordFileName);
                    new InstallationRecord(ownsDirectory, files).WriteTo(recordFile);
                    transaction.Put(recordFile, ProductInfo.RecordFileName);
                    transaction.Commit();
                }
                catch (Exception ex) when (!(ex is SetupException))
                {
                    log.Write("Installation fehlgeschlagen, Rücknahme: " + ex);
                    var restored = transaction.Rollback();
                    if (createdDirectory) NativeFiles.TryRemoveEmptyDirectory(directory);
                    throw new SetupException(text.InstallFailed(ex.Message) + (restored ? " " + text.RolledBack : string.Empty),
                        restored, ex);
                }

                progress?.Report(text.ProgressRegistering);
                Register(package, directory, options);
                RemoveLegacyInstallerState(directory);
                log.Write("Installation abgeschlossen.");
                return new InstallResult(directory);
            }
            finally
            {
                TryDeleteStaging(staging);
            }
        }

        private void Register(PayloadPackage package, string directory, InstallOptions options)
        {
            var registry = _context.Registry;
            var appExecutable = ProductInfo.AppExecutable(directory);
            var setupExecutable = Path.Combine(directory, ProductInfo.SetupExeName);
            var setupSize = File.Exists(setupExecutable) ? new FileInfo(setupExecutable).Length : 0;
            var key = ProductInfo.UninstallKey;
            registry.SetString(key, "DisplayName", ProductInfo.AppName);
            registry.SetString(key, "DisplayVersion", package.Manifest.Version);
            registry.SetString(key, "DisplayIcon", appExecutable);
            registry.SetString(key, "InstallLocation", directory);
            registry.SetString(key, "UninstallString", "\"" + setupExecutable + "\" --uninstall");
            registry.SetString(key, "InstallDate", _context.Now().ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            registry.SetDword(key, "NoModify", 1);
            registry.SetDword(key, "NoRepair", 1);
            registry.SetDword(key, "EstimatedSize", (int)Math.Min(int.MaxValue, (package.Manifest.TotalSize + setupSize + 1023) / 1024));

            var locations = _context.Locations;
            _context.Shortcuts.Create(locations.StartMenuShortcut, appExecutable, directory, appExecutable);
            if (options.DesktopShortcut)
                _context.Shortcuts.Create(locations.DesktopShortcut, appExecutable, directory, appExecutable);
            else
                NativeFiles.TryDelete(locations.DesktopShortcut, _context.Log);

            // Only this installation's own Run command is ever written or removed.
            if (options.Autostart)
                registry.SetString(ProductInfo.RunKey, ProductInfo.RunValueName, ProductInfo.RunCommand(directory));
            else if (ProductInfo.IsOwnRunCommand(registry.GetString(ProductInfo.RunKey, ProductInfo.RunValueName), directory))
                registry.DeleteValue(ProductInfo.RunKey, ProductInfo.RunValueName);
        }

        /// <summary>Takes over an installation of the former Inno Setup based installer.</summary>
        private void RemoveLegacyInstallerState(string directory)
        {
            foreach (var file in Directory.GetFiles(directory))
            {
                var name = Path.GetFileName(file);
                if (ProductInfo.IsInnoUninstallerFile(name)
                    || ProductInfo.LegacyFiles.Contains(name, StringComparer.OrdinalIgnoreCase))
                    NativeFiles.TryDelete(file, _context.Log);
            }
            if (_context.Registry.KeyExists(ProductInfo.InnoUninstallKey))
            {
                _context.Registry.DeleteKey(ProductInfo.InnoUninstallKey);
                _context.Log.Write("Frühere Inno-Setup-Registrierung übernommen.");
            }
        }

        private void TryDeleteStaging(string staging)
        {
            // Only the directory this run created under the temp root is removed.
            try
            {
                if (Directory.Exists(staging) && !SetupPaths.IsReparsePoint(staging)
                    && Path.GetFileName(staging).StartsWith(ProductInfo.TempDirectoryPrefix, StringComparison.Ordinal))
                    Directory.Delete(staging, true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _context.Log.Write("Temporäre Setup-Dateien bleiben erhalten: " + staging);
            }
        }
    }
}
