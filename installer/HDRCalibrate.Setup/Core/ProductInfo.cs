using System;
using System.IO;
using System.Text.RegularExpressions;

namespace HDRCalibrate.Setup.Core
{
    /// <summary>Fixed identity shared with earlier (Inno Setup based) releases.</summary>
    public static class ProductInfo
    {
        public const string AppName = "HDR Calibrate";
        public const string AppExeName = "HDRCalibrate.exe";
        public const string SetupExeName = "HDRCalibrate.Setup.exe";
        public const string RecordFileName = "HDRCalibrate.Setup.record";
        public const string AppId = "{6D421644-5F94-4C3D-8F1D-872DB1D5778B}";
        public const string UninstallKeyRoot = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
        public const string UninstallKey = UninstallKeyRoot + @"\" + AppId;
        // Inno Setup appends "_is1" to the AppId of the uninstall entry.
        public const string InnoUninstallKey = UninstallKey + "_is1";
        public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string RunValueName = "HDRCalibrate";
        // Same name as the former Inno SetupMutex: old and new setups exclude each other.
        public const string SetupMutexName = "HDRCalibrate.Setup.6D4216445F944C3D8F1D872DB1D5778B";
        public const string ShortcutFileName = AppName + ".lnk";
        public const string NativeDirectoryName = "Native";
        public const string NativeHookFileName = "HDRCalibrate.SelectiveDwmHook.dll";
        public const string NativeControllerFileName = "HDRCalibrate.SelectiveController.exe";
        public const string NativeLicenseFileName = "MinHook-LICENSE.txt";
        public const string BackupDirectoryPrefix = ".setup-backup-";
        public const string TempDirectoryPrefix = "HDRCalibrate.Setup-";
        public const int DotNet481Release = 533320;

        /// <summary>Files of earlier installer generations that are owned by this product.</summary>
        public static readonly string[] LegacyFiles =
        {
            "HDRCalibrate.SetupSupport.exe",
            "HDRCalibrate.SetupSupport.exe.config"
        };

        private static readonly Regex InnoUninstallerFilePattern =
            new Regex(@"\Aunins\d{3}\.(exe|dat|msg)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static bool IsInnoUninstallerFile(string fileName) =>
            fileName != null && InnoUninstallerFilePattern.IsMatch(fileName);

        public static string AppExecutable(string installDirectory) =>
            Path.Combine(installDirectory, AppExeName);

        public static string RunCommand(string installDirectory) =>
            "\"" + AppExecutable(installDirectory) + "\" --tray --startup";

        public static string LegacyRunCommand(string installDirectory) =>
            "\"" + AppExecutable(installDirectory) + "\" --tray";

        public static bool IsOwnRunCommand(string value, string installDirectory) =>
            value != null
            && (string.Equals(value, RunCommand(installDirectory), StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, LegacyRunCommand(installDirectory), StringComparison.OrdinalIgnoreCase));
    }
}
