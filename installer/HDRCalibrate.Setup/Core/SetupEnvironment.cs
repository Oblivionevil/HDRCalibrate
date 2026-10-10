using System;
using System.IO;

namespace HDRCalibrate.Setup.Core
{
    /// <summary>A failure with a message that is shown to the user as-is.</summary>
    public sealed class SetupException : Exception
    {
        public SetupException(string message, bool changesRolledBack = false, Exception inner = null)
            : base(message, inner)
        {
            ChangesRolledBack = changesRolledBack;
        }

        public bool ChangesRolledBack { get; }
    }

    public enum SetupMode { Install, Uninstall }

    public static class SetupCommandLine
    {
        public const string UninstallArgument = "--uninstall";

        /// <summary>Exact, case-insensitive parsing. Anything else is rejected.</summary>
        public static bool TryParse(string[] args, out SetupMode mode)
        {
            mode = SetupMode.Install;
            if (args == null || args.Length == 0) return true;
            if (args.Length == 1 && string.Equals(args[0], UninstallArgument, StringComparison.OrdinalIgnoreCase))
            {
                mode = SetupMode.Uninstall;
                return true;
            }
            return false;
        }
    }

    /// <summary>HKCU access only; the setup never writes machine-wide state.</summary>
    public interface ISetupRegistry
    {
        string GetString(string keyPath, string valueName);
        bool KeyExists(string keyPath);
        void SetString(string keyPath, string valueName, string value);
        void SetDword(string keyPath, string valueName, int value);
        void DeleteValue(string keyPath, string valueName);
        void DeleteKey(string keyPath);
    }

    public interface IShortcutWriter
    {
        void Create(string linkPath, string targetPath, string workingDirectory, string iconPath);
    }

    /// <summary>Asks a running HDR Calibrate instance to exit; never terminates a process.</summary>
    public interface IAppStopper
    {
        /// <returns>0 if the target is no longer running, otherwise a failure code.</returns>
        int Stop(string commandExecutable, string targetExecutable);
    }

    public interface ISetupLog
    {
        void Write(string message);
    }

    public sealed class NullSetupLog : ISetupLog
    {
        public void Write(string message) { }
    }

    public sealed class SetupLocations
    {
        public SetupLocations(string defaultInstallDirectory, string startMenuDirectory,
            string desktopDirectory, string tempDirectory)
        {
            DefaultInstallDirectory = Path.GetFullPath(defaultInstallDirectory);
            StartMenuDirectory = Path.GetFullPath(startMenuDirectory);
            DesktopDirectory = Path.GetFullPath(desktopDirectory);
            TempDirectory = Path.GetFullPath(tempDirectory);
        }

        public string DefaultInstallDirectory { get; }
        /// <summary>The program group, e.g. ...\Start Menu\Programs\HDR Calibrate.</summary>
        public string StartMenuDirectory { get; }
        public string DesktopDirectory { get; }
        public string TempDirectory { get; }

        public string StartMenuShortcut => Path.Combine(StartMenuDirectory, ProductInfo.ShortcutFileName);
        public string DesktopShortcut => Path.Combine(DesktopDirectory, ProductInfo.ShortcutFileName);

        public static SetupLocations FromSystem() => new SetupLocations(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", ProductInfo.AppName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), ProductInfo.AppName),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Path.GetTempPath());
    }

    public sealed class SetupContext
    {
        public SetupContext(SetupLocations locations, ISetupRegistry registry, IShortcutWriter shortcuts,
            IAppStopper stopper, SetupText text, ISetupLog log, Func<DateTime> now = null)
        {
            Locations = locations ?? throw new ArgumentNullException(nameof(locations));
            Registry = registry ?? throw new ArgumentNullException(nameof(registry));
            Shortcuts = shortcuts ?? throw new ArgumentNullException(nameof(shortcuts));
            Stopper = stopper ?? throw new ArgumentNullException(nameof(stopper));
            Text = text ?? throw new ArgumentNullException(nameof(text));
            Log = log ?? new NullSetupLog();
            Now = now ?? (() => DateTime.Now);
        }

        public SetupLocations Locations { get; }
        public ISetupRegistry Registry { get; }
        public IShortcutWriter Shortcuts { get; }
        public IAppStopper Stopper { get; }
        public SetupText Text { get; }
        public ISetupLog Log { get; }
        public Func<DateTime> Now { get; }
    }
}
