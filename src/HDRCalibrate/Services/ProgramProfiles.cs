using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Xml;
using System.Xml.Serialization;

namespace HDRCalibrate.Services
{
    public enum ProgramColorMode
    {
        Default = 0,
        Srgb = 1,
        ScrgbLinear = 2
    }

    public sealed class ProgramLaunchProfile
    {
        public string ExecutablePath { get; set; }
        public ProgramColorMode ColorMode { get; set; }
        // Stored eligibility only. Loading or launching a profile never activates gamma.
        public bool ForceSdrGamma { get; set; }

        public ProgramLaunchProfile Copy() => new ProgramLaunchProfile
        {
            ExecutablePath = ExecutablePath,
            ColorMode = ColorMode,
            ForceSdrGamma = ForceSdrGamma
        };
    }

    public sealed class ProgramProfilesDocument
    {
        public int SchemaVersion { get; set; } = 1;
        public List<ProgramLaunchProfile> Profiles { get; set; } = new List<ProgramLaunchProfile>();
    }

    public static class ProgramProfileRules
    {
        public static string NormalizePath(string path)
        {
            // Only explicit local executable paths; never commands, URLs or shell documents.
            if (string.IsNullOrWhiteSpace(path) || path.Length > 260
                || path.Any(char.IsControl) || path.IndexOf('"') >= 0
                || path.Length < 4 || !char.IsLetter(path[0]) || path[1] != ':'
                || path[2] != '\\' || path.IndexOf(':', 2) >= 0
                || !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Bitte einen vollständigen lokalen EXE-Pfad auswählen.");
            return Path.GetFullPath(path);
        }

        public static string Arguments(ProgramColorMode mode)
        {
            switch (mode)
            {
                case ProgramColorMode.Default: return string.Empty;
                case ProgramColorMode.Srgb: return "--force-color-profile=srgb";
                case ProgramColorMode.ScrgbLinear: return "--force-color-profile=scrgb-linear";
                default: throw new ArgumentException("Unbekannter Farbraum-Modus.");
            }
        }

        public static string Label(ProgramColorMode mode)
        {
            switch (mode)
            {
                case ProgramColorMode.Default: return "Standard (nicht erzwingen)";
                case ProgramColorMode.Srgb: return "SDR / sRGB erzwingen";
                case ProgramColorMode.ScrgbLinear: return "scRGB / HDR-fähig festlegen";
                default: return "Ungültiges Profil";
            }
        }

        public static string Description(ProgramColorMode mode)
        {
            switch (mode)
            {
                case ProgramColorMode.Default:
                    return "Start ohne Farbraum-Parameter. Die Anwendung entscheidet selbst; Gammawechsel bleiben möglich.";
                case ProgramColorMode.Srgb:
                    return "Erzwingt das SDR-Farbprofil nur in dieser Anwendung. HDR und Wide Gamut können dort eingeschränkt werden. Die DWM-Gammakorrektur muss separat aktiv sein; Overlay-Pfade können sie weiterhin umgehen.";
                case ProgramColorMode.ScrgbLinear:
                    return "Legt einen HDR-fähigen Farbraum fest und kann das Umschalten vermeiden. Die SDR-Gammakorrektur von HDR Calibrate greift in dieser Ausgabe nicht mehr. Auch die Helligkeitszuordnung kann sich ändern.";
                default: throw new ArgumentException("Unbekannter Farbraum-Modus.");
            }
        }

        public static List<ProgramLaunchProfile> Validate(IEnumerable<ProgramLaunchProfile> profiles)
        {
            if (profiles == null) throw new InvalidDataException("Die Programmliste fehlt.");
            var result = new List<ProgramLaunchProfile>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var profile in profiles)
            {
                if (profile == null) throw new InvalidDataException("Ein Programmprofil fehlt.");
                var path = NormalizePath(profile.ExecutablePath);
                Arguments(profile.ColorMode);
                if (!paths.Add(path)) throw new InvalidDataException("Das Programm steht mehrfach in der Liste.");
                if (result.Count >= 128) throw new InvalidDataException("Maximal 128 Programme sind erlaubt.");
                result.Add(new ProgramLaunchProfile
                {
                    ExecutablePath = path,
                    ColorMode = profile.ColorMode,
                    ForceSdrGamma = profile.ForceSdrGamma
                });
            }
            return result;
        }
    }

    public sealed class ProgramProfileStore
    {
        private readonly string _path;
        private readonly XmlSerializer _serializer = new XmlSerializer(typeof(ProgramProfilesDocument));

        public ProgramProfileStore(string baseDirectory = null)
        {
            _path = Path.Combine(baseDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HDRCalibrate"),
                "program-profiles.xml");
        }

        public List<ProgramLaunchProfile> Load()
        {
            if (!File.Exists(_path)) return new List<ProgramLaunchProfile>();
            try
            {
                using (var stream = File.OpenRead(_path))
                using (var reader = XmlReader.Create(stream, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = 1024 * 1024
                }))
                {
                    var document = (ProgramProfilesDocument)_serializer.Deserialize(reader);
                    if (document == null || document.SchemaVersion != 1)
                        throw new InvalidDataException("Die Version der Programmliste wird nicht unterstützt.");
                    return ProgramProfileRules.Validate(document.Profiles);
                }
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidDataException("Die gespeicherte Programmliste ist beschädigt. Sie wurde nicht überschrieben.", ex);
            }
        }

        public void Save(IEnumerable<ProgramLaunchProfile> profiles)
        {
            var document = new ProgramProfilesDocument { Profiles = ProgramProfileRules.Validate(profiles) };
            var directory = Path.GetDirectoryName(_path);
            Directory.CreateDirectory(directory);
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    _serializer.Serialize(stream, document);
                    stream.Flush(true);
                }
                if (File.Exists(_path)) File.Replace(temporary, _path, _path + ".bak", true);
                else File.Move(temporary, _path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }

    public sealed class ProgramExecutableInfo
    {
        public string Name { get; set; }
        public string Version { get; set; }
        public bool IsSupported { get; set; }
        public string Status { get; set; }
    }

    public static class ProgramExecutableInspector
    {
        // This is a compatibility check, not an authenticity/security attestation.
        public static string Recognize(string fileName, string productName, string originalFileName)
        {
            if (string.Equals(fileName, "brave.exe", StringComparison.OrdinalIgnoreCase)
                && string.Equals(productName, "Brave Browser", StringComparison.OrdinalIgnoreCase)) return "Brave";
            if (string.Equals(fileName, "msedge.exe", StringComparison.OrdinalIgnoreCase)
                && string.Equals(productName, "Microsoft Edge", StringComparison.OrdinalIgnoreCase)) return "Microsoft Edge";
            if ((string.Equals(fileName, "chrome.exe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, "chromium.exe", StringComparison.OrdinalIgnoreCase))
                && string.Equals(originalFileName, "chrome.exe", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(productName, "Google Chrome", StringComparison.OrdinalIgnoreCase)) return "Google Chrome";
                if (string.Equals(productName, "Chromium", StringComparison.OrdinalIgnoreCase)) return "Chromium";
            }
            return null;
        }

        public static ProgramExecutableInfo Inspect(string executablePath)
        {
            var path = ProgramProfileRules.NormalizePath(executablePath);
            var result = new ProgramExecutableInfo { Name = Path.GetFileNameWithoutExtension(path) };
            if (!File.Exists(path)) { result.Status = "EXE fehlt – Start gesperrt"; return result; }
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                var name = Recognize(Path.GetFileName(path), info.ProductName, info.OriginalFilename);
                result.Name = name ?? result.Name;
                result.Version = info.ProductVersion;
                result.IsSupported = name != null;
                result.Status = name == null ? "Selbst starten · SDR-Gamma separat testen"
                    : "Chromium-Startparameter · " + result.Version;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is Win32Exception)
            {
                result.Status = "EXE nicht prüfbar – Start gesperrt";
            }
            return result;
        }
    }

    public sealed class ProgramProfileLauncher
    {
        private readonly Func<string, ProgramExecutableInfo> _inspect;
        private readonly Func<string, bool> _isRunning;
        private readonly Func<bool> _isElevated;
        private readonly Action<ProcessStartInfo> _start;
        private readonly Action<ProcessStartInfo> _startFromExplorer;

        public ProgramProfileLauncher(Func<string, ProgramExecutableInfo> inspect = null,
            Func<string, bool> isRunning = null, Action<ProcessStartInfo> start = null,
            Func<bool> isElevated = null, Action<ProcessStartInfo> startFromExplorer = null)
        {
            _inspect = inspect ?? ProgramExecutableInspector.Inspect;
            _isRunning = isRunning ?? IsRunning;
            _startFromExplorer = startFromExplorer ?? ExplorerProgramLauncher.Start;
            _isElevated = isElevated ?? (() =>
            {
                using (var identity = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            });
            _start = start ?? (info =>
            {
                using (var process = Process.Start(info))
                    if (process == null) throw new InvalidOperationException("Der Programmstart wurde nicht bestätigt.");
            });
        }

        public ProcessStartInfo BuildStartInfo(ProgramLaunchProfile profile)
        {
            var validated = ProgramProfileRules.Validate(new[] { profile }).Single();
            if (!_inspect(validated.ExecutablePath).IsSupported)
                throw new InvalidOperationException("Die EXE ist nicht als unterstützter Chromium-Browser erkennbar.");
            return new ProcessStartInfo
            {
                FileName = validated.ExecutablePath,
                Arguments = ProgramProfileRules.Arguments(validated.ColorMode),
                WorkingDirectory = Path.GetDirectoryName(validated.ExecutablePath),
                UseShellExecute = false
            };
        }

        public void Launch(ProgramLaunchProfile profile)
        {
            var info = BuildStartInfo(profile);
            if (_isRunning(Path.GetFileNameWithoutExtension(info.FileName)))
                throw new InvalidOperationException("Das Programm läuft bereits. Bitte vollständig selbst beenden (auch Hintergrundprozesse) und erneut über dieses Profil starten. Es wird kein Prozess beendet.");
            // Prefer the interactive desktop user's context for an elevated app.
            // With UAC disabled the desktop can also be elevated; that is allowed.
            // Never retry through a second mechanism after an ambiguous COM failure.
            if (_isElevated()) _startFromExplorer(info);
            else _start(info);
        }

        private static bool IsRunning(string processName)
        {
            var processes = Process.GetProcessesByName(processName);
            try { return processes.Length != 0; }
            finally { foreach (var process in processes) process.Dispose(); }
        }
    }
}
