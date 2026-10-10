using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HDRCalibrate.Setup.Core
{
    public sealed class ExistingInstallation
    {
        public string Directory { get; internal set; }
        public string Version { get; internal set; }
        public bool HasOwnEntry { get; internal set; }
        public bool HasInnoEntry { get; internal set; }
        public bool AppExists { get; internal set; }
        public bool AutostartRegistered { get; internal set; }
        public bool DesktopShortcutExists { get; internal set; }
        public bool IsInstalled => AppExists || HasOwnEntry || HasInnoEntry;
    }

    public static class InstallationLocator
    {
        /// <summary>
        /// The installation directory is fixed per user. Only an existing installation
        /// (this setup or the former Inno Setup) may keep its recorded directory.
        /// </summary>
        public static ExistingInstallation Find(SetupContext context)
        {
            var registry = context.Registry;
            var result = new ExistingInstallation
            {
                HasOwnEntry = registry.KeyExists(ProductInfo.UninstallKey),
                HasInnoEntry = registry.KeyExists(ProductInfo.InnoUninstallKey)
            };
            var candidates = new[]
            {
                registry.GetString(ProductInfo.UninstallKey, "InstallLocation"),
                registry.GetString(ProductInfo.InnoUninstallKey, "InstallLocation"),
                registry.GetString(ProductInfo.InnoUninstallKey, "Inno Setup: App Path")
            };
            result.Directory = candidates.Select(Normalize).FirstOrDefault(path => path != null)
                ?? context.Locations.DefaultInstallDirectory;
            result.Version = registry.GetString(ProductInfo.UninstallKey, "DisplayVersion")
                ?? registry.GetString(ProductInfo.InnoUninstallKey, "DisplayVersion");
            result.AppExists = File.Exists(ProductInfo.AppExecutable(result.Directory));
            result.AutostartRegistered = ProductInfo.IsOwnRunCommand(
                registry.GetString(ProductInfo.RunKey, ProductInfo.RunValueName), result.Directory);
            result.DesktopShortcutExists = File.Exists(context.Locations.DesktopShortcut);
            return result;
        }

        /// <summary>A local, absolute directory that is not a reparse point; otherwise null.</summary>
        public static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                var trimmed = path.Trim().TrimEnd('\\');
                if (trimmed.Length < 3 || !char.IsLetter(trimmed[0]) || trimmed[1] != ':' || trimmed[2] != '\\'
                    || trimmed.IndexOfAny(new[] { '"', '*', '?', '<', '>', '|' }) >= 0 || trimmed.IndexOf(':', 2) >= 0)
                    return null;
                var full = Path.GetFullPath(trimmed);
                if (Directory.Exists(full) && SetupPaths.IsReparsePoint(full)) return null;
                return full;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is IOException
                || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        public static bool SamePath(string left, string right) =>
            string.Equals(Path.GetFullPath(left).TrimEnd('\\'), Path.GetFullPath(right).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);

        public static string StopMessage(SetupText text, int code)
        {
            switch (code)
            {
                case ProcessAppStopper.ErrorInvalidParameter: return text.StopStartFailed;
                case ProcessAppStopper.AppExitTimeout:
                case ProcessAppStopper.ErrorTimeout: return text.StopTimedOut;
                case ProcessAppStopper.ErrorBusy: return text.StopBusy;
                default: return text.StopFailed(code);
            }
        }
    }

    public static class NativeFiles
    {
        /// <summary>
        /// Removes every native version folder below the installation. A locked hook
        /// DLL keeps its whole folder (with the license); nothing is deferred to a
        /// reboot, no process is stopped and reparse points are never followed.
        /// </summary>
        public static IReadOnlyList<string> RemoveAll(string installDirectory, ISetupLog log)
        {
            var retained = new List<string>();
            var root = Path.Combine(installDirectory, ProductInfo.NativeDirectoryName);
            if (!Directory.Exists(root) || SetupPaths.IsReparsePoint(root)) return retained;
            foreach (var directory in Directory.GetDirectories(root))
            {
                if (SetupPaths.IsReparsePoint(directory)) continue;
                if (!TryDelete(Path.Combine(directory, ProductInfo.NativeHookFileName), log))
                {
                    log.Write("Geladene native Gamma-DLL bleibt erhalten: " + directory);
                    retained.Add(directory);
                    continue;
                }
                TryDelete(Path.Combine(directory, ProductInfo.NativeControllerFileName), log);
                TryDelete(Path.Combine(directory, ProductInfo.NativeLicenseFileName), log);
                TryRemoveEmptyDirectory(directory);
            }
            TryRemoveEmptyDirectory(root);
            return retained;
        }

        internal static bool TryDelete(string path, ISetupLog log)
        {
            if (!File.Exists(path)) return true;
            if (SetupPaths.IsReparsePoint(path)) return false;
            try
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                log.Write("Nicht löschbar: " + path + " (" + ex.Message + ")");
                return false;
            }
        }

        internal static void TryRemoveEmptyDirectory(string directory)
        {
            try
            {
                if (Directory.Exists(directory) && !SetupPaths.IsReparsePoint(directory)
                    && !Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory, false);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }
    }
}
