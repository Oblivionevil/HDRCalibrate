using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;

namespace HDRCalibrate.Services
{
    public interface IRunValueStore
    {
        string Read(string valueName);
        void Write(string valueName, string value);
        void Delete(string valueName);
    }

    internal sealed class RegistryRunValueStore : IRunValueStore
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public string Read(string valueName)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
            {
                return key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            }
        }

        public void Write(string valueName, string value)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true))
            {
                if (key == null) throw new InvalidOperationException("Der per-user Autostart-Schlüssel konnte nicht geöffnet werden.");
                key.SetValue(valueName, value, RegistryValueKind.String);
            }
        }

        public void Delete(string valueName)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
            {
                key?.DeleteValue(valueName, false);
            }
        }
    }

    public sealed class AutostartService
    {
        public const string ValueName = "HDRCalibrate";
        public const string TrayArgument = "--tray";
        public const string StartupArgument = "--startup";

        private readonly IRunValueStore _store;
        private readonly string _commandLine;
        private readonly string _legacyCommandLine;
        private readonly Exception _initializationError;

        public AutostartService()
        {
            _store = new RegistryRunValueStore();
            try
            {
                var executablePath = GetCurrentExecutablePath();
                _commandLine = BuildCommandLine(executablePath);
                _legacyCommandLine = BuildLegacyCommandLine(executablePath);
            }
            catch (Exception ex)
            {
                _initializationError = ex;
            }
        }

        public AutostartService(IRunValueStore store, string executablePath)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _commandLine = BuildCommandLine(executablePath);
            _legacyCommandLine = BuildLegacyCommandLine(executablePath);
        }

        public string CommandLine
        {
            get
            {
                EnsureInitialized();
                return _commandLine;
            }
        }

        public bool IsEnabled()
        {
            EnsureInitialized();
            var current = _store.Read(ValueName);
            return string.Equals(current, _commandLine, StringComparison.Ordinal)
                || string.Equals(current, _legacyCommandLine, StringComparison.Ordinal);
        }

        public bool NeedsMigration()
        {
            EnsureInitialized();
            return string.Equals(_store.Read(ValueName), _legacyCommandLine, StringComparison.Ordinal);
        }

        public void SetEnabled(bool enabled)
        {
            EnsureInitialized();
            if (enabled) _store.Write(ValueName, _commandLine);
            else _store.Delete(ValueName);
        }

        public static string BuildCommandLine(string executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
                throw new ArgumentException("Ein ausführbarer Programmpfad ist erforderlich.", nameof(executablePath));
            if (executablePath.IndexOf('"') >= 0)
                throw new ArgumentException("Der Programmpfad darf keine Anführungszeichen enthalten.", nameof(executablePath));
            if (!Path.IsPathRooted(executablePath))
                throw new ArgumentException("Der Programmpfad muss absolut sein.", nameof(executablePath));

            var fullPath = Path.GetFullPath(executablePath);
            var commandLine = "\"" + fullPath + "\" " + TrayArgument + " " + StartupArgument;
            if (commandLine.Length > 260)
                throw new ArgumentException("Der Autostart-Befehl darf höchstens 260 Zeichen lang sein.", nameof(executablePath));
            return commandLine;
        }

        public static string BuildLegacyCommandLine(string executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
                throw new ArgumentException("Ein ausführbarer Programmpfad ist erforderlich.", nameof(executablePath));
            if (executablePath.IndexOf('"') >= 0)
                throw new ArgumentException("Der Programmpfad darf keine Anführungszeichen enthalten.", nameof(executablePath));
            if (!Path.IsPathRooted(executablePath))
                throw new ArgumentException("Der Programmpfad muss absolut sein.", nameof(executablePath));
            return "\"" + Path.GetFullPath(executablePath) + "\" " + TrayArgument;
        }

        private static string GetCurrentExecutablePath()
        {
            using (var process = Process.GetCurrentProcess())
            {
                var path = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(path))
                    throw new InvalidOperationException("Der Pfad der laufenden Anwendung konnte nicht ermittelt werden.");
                return path;
            }
        }

        private void EnsureInitialized()
        {
            if (_initializationError != null)
                throw new InvalidOperationException("Autostart ist für diesen Programmpfad nicht verfügbar: " + _initializationError.Message,
                    _initializationError);
        }
    }
}
