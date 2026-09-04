using Microsoft.Win32;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace HDRCalibrate.Dwm
{
    [Serializable]
    public sealed class WindowsEnvironmentIdentity
    {
        public int OsBuildNumber { get; set; }
        public int UpdateBuildRevision { get; set; } = -1;
        public string BuildLabEx { get; set; }
        public string DwmExecutableSha256 { get; set; }
        public string DwmCoreSha256 { get; set; }

        public void Validate()
        {
            if (OsBuildNumber <= 0 || UpdateBuildRevision < 0
                || string.IsNullOrWhiteSpace(BuildLabEx)
                || BuildLabEx.Length > 256
                || !string.Equals(BuildLabEx, BuildLabEx.Trim(), StringComparison.Ordinal)
                || BuildLabEx.Any(char.IsControl))
                throw new InvalidDataException("Die Windows-Buildidentität fehlt oder ist ungültig.");
            if (!IsSha256(DwmExecutableSha256) || !IsSha256(DwmCoreSha256))
                throw new InvalidDataException("Ein Windows-DWM-Dateifingerabdruck fehlt oder ist ungültig.");
        }

        public bool Matches(WindowsEnvironmentIdentity other)
        {
            if (other == null) return false;
            try
            {
                Validate();
                other.Validate();
                return OsBuildNumber == other.OsBuildNumber
                    && UpdateBuildRevision == other.UpdateBuildRevision
                    && string.Equals(BuildLabEx, other.BuildLabEx, StringComparison.Ordinal)
                    && string.Equals(DwmExecutableSha256, other.DwmExecutableSha256, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(DwmCoreSha256, other.DwmCoreSha256, StringComparison.OrdinalIgnoreCase);
            }
            catch (InvalidDataException)
            {
                return false;
            }
        }

        public WindowsEnvironmentIdentity Clone()
        {
            return new WindowsEnvironmentIdentity
            {
                OsBuildNumber = OsBuildNumber,
                UpdateBuildRevision = UpdateBuildRevision,
                BuildLabEx = BuildLabEx,
                DwmExecutableSha256 = DwmExecutableSha256,
                DwmCoreSha256 = DwmCoreSha256
            };
        }

        private static bool IsSha256(string value)
        {
            return value != null && value.Length == 64
                && value.All(character =>
                    (character >= '0' && character <= '9')
                    || (character >= 'A' && character <= 'F')
                    || (character >= 'a' && character <= 'f'));
        }
    }

    public interface IWindowsEnvironmentIdentityProvider
    {
        WindowsEnvironmentIdentity Capture();
    }

    public static class WindowsEnvironmentGuard
    {
        public static void RequireCurrent(
            WindowsEnvironmentIdentity expected,
            IWindowsEnvironmentIdentityProvider provider = null)
        {
            if (expected == null)
                throw new InvalidDataException("Die Windows-Identität des freigegebenen Tests fehlt. Automatisches Anwenden ist gesperrt.");
            expected.Validate();
            var current = (provider ?? new WindowsEnvironmentIdentityProvider()).Capture();
            if (!expected.Matches(current))
                throw new InvalidDataException("Windows oder DWM wurde seit dem freigegebenen Test geändert. Erneuter Test und ausdrückliche Freigabe erforderlich.");
        }
    }

    public sealed class WindowsEnvironmentIdentityProvider : IWindowsEnvironmentIdentityProvider
    {
        private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

        public WindowsEnvironmentIdentity Capture()
        {
            // Read the actual installed OS identity; version-compatibility shims must not
            // make a post-update environment look like the previously tested environment.
            var identity = ReadBuildIdentity();
            var systemDirectory = Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Sysnative")
                : Environment.SystemDirectory;
            if (string.IsNullOrWhiteSpace(systemDirectory))
                throw new InvalidDataException("Das native Windows-Systemverzeichnis ist nicht lesbar.");

            // Keep both files open without write/delete sharing until the complete
            // snapshot is finished, and detect OS metadata changing during capture.
            using (var dwmExecutable = OpenComponent(Path.Combine(systemDirectory, "dwm.exe")))
            using (var dwmCore = OpenComponent(Path.Combine(systemDirectory, "dwmcore.dll")))
            {
                identity.DwmExecutableSha256 = ComputeSha256(dwmExecutable);
                identity.DwmCoreSha256 = ComputeSha256(dwmCore);
                var after = ReadBuildIdentity();
                if (identity.OsBuildNumber != after.OsBuildNumber
                    || identity.UpdateBuildRevision != after.UpdateBuildRevision
                    || !string.Equals(identity.BuildLabEx, after.BuildLabEx, StringComparison.Ordinal))
                    throw new InvalidDataException("Windows wurde während der Sicherheitsprüfung geändert. Bitte erneut testen.");
            }

            identity.Validate();
            return identity;
        }

        private static WindowsEnvironmentIdentity ReadBuildIdentity()
        {
            using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = machine.OpenSubKey(CurrentVersionKey, false))
            {
                if (key == null)
                    throw new InvalidDataException("Die aktuelle Windows-Buildidentität ist nicht lesbar.");
                var build = key.GetValue("CurrentBuildNumber", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                var revision = key.GetValue("UBR", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                var buildLabEx = key.GetValue("BuildLabEx", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                int buildNumber;
                if (!int.TryParse(build, NumberStyles.None, CultureInfo.InvariantCulture, out buildNumber)
                    || buildNumber <= 0 || !(revision is int) || (int)revision < 0
                    || string.IsNullOrWhiteSpace(buildLabEx))
                    throw new InvalidDataException("Windows-Build, Updaterevision oder BuildLabEx ist nicht vollständig lesbar.");
                return new WindowsEnvironmentIdentity
                {
                    OsBuildNumber = buildNumber,
                    UpdateBuildRevision = (int)revision,
                    BuildLabEx = buildLabEx
                };
            }
        }

        private static FileStream OpenComponent(string path)
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 0) return stream;
            stream.Dispose();
            throw new InvalidDataException("Die Windows-DWM-Komponente ist leer: " + path);
        }

        private static string ComputeSha256(Stream stream)
        {
            using (var sha256 = SHA256.Create())
                return BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", string.Empty);
        }
    }
}
