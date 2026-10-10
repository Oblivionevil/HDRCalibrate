using HDRCalibrate.Core;
using HDRCalibrate.Dwm;
using Microsoft.Win32;
using System;
using System.Globalization;
using System.IO;
using System.Threading;

namespace HDRCalibrate.Services
{
    /// <summary>
    /// An immutable snapshot of a supervised apply committed in this app instance.
    /// Its liveness can only be revoked; saved DWM state never creates this proof.
    /// </summary>
    public sealed class ProgramGammaApproval
    {
        private readonly WindowsEnvironmentIdentity _windowsEnvironment;
        private readonly Func<bool> _isStillValid;
        private int _revoked;

        private ProgramGammaApproval(DwmPatchResponse committed, PersistedDwmState state,
            WindowsEnvironmentIdentity windowsEnvironment, Func<bool> isStillValid)
        {
            DwmProcessId = committed.DwmProcessId;
            DwmStartTimeUtcTicks = committed.DwmStartTimeUtcTicks;
            DwmCreationFileTime = ToCreationFileTime(DwmStartTimeUtcTicks);
            SessionId = state.SessionId;
            SettingsKey = CalibrationSettingsIdentity.Compute(state.Settings);
            Gamma = state.Settings.Gamma;
            Brightness = state.Settings.Brightness;
            PlanFingerprint = committed.PatchPlanFingerprint;
            DwmCoreVersion = committed.DwmCoreVersion;
            _windowsEnvironment = windowsEnvironment.Clone();
            _isStillValid = isStillValid;
        }

        public int DwmProcessId { get; }
        public long DwmStartTimeUtcTicks { get; }
        public long DwmCreationFileTime { get; }
        public int SessionId { get; }
        public string SettingsKey { get; }
        public double Gamma { get; }
        public double Brightness { get; }
        public string PlanFingerprint { get; }
        public string DwmCoreVersion { get; }
        public WindowsEnvironmentIdentity WindowsEnvironment => _windowsEnvironment.Clone();

        // This is pure evidence validation, also usable with injected DTOs in tests.
        // The production caller is MainViewModel, after ApplySupervised returned.
        public static ProgramGammaApproval CreateFromCommittedApply(DwmPatchResponse committed,
            PersistedDwmState state, WindowsEnvironmentIdentity approvedWindowsEnvironment,
            WindowsEnvironmentIdentity currentWindowsEnvironment, Func<bool> isStillValid)
        {
            ValidateCommittedState(committed, state, approvedWindowsEnvironment, currentWindowsEnvironment);
            if (isStillValid == null) throw new ArgumentNullException(nameof(isStillValid));
            return new ProgramGammaApproval(committed, state, approvedWindowsEnvironment, isStillValid);
        }

        public static void ValidateCommittedState(DwmPatchResponse committed, PersistedDwmState state,
            WindowsEnvironmentIdentity approvedWindowsEnvironment,
            WindowsEnvironmentIdentity currentWindowsEnvironment)
        {
            SupervisedApplyProtocol.ValidateResponse(committed, committed?.CorrelationToken, true);
            if (committed.RecoveryPerformed || committed.LivePatchBlocked
                || committed.PatchedShaderCount <= 0 || committed.PatchedConstantVectorCount <= 0
                || committed.ChangedByteCount <= 0)
                throw new InvalidDataException("Die bestätigte DWM-Antwort enthält keine aktive SDR-Korrektur.");
            ToCreationFileTime(committed.DwmStartTimeUtcTicks);
            if (state == null || !state.WasApplied || state.Settings == null || state.SessionId < 0
                || state.DwmStartTimeUtcTicks <= 0
                || state.DwmProcessId != committed.DwmProcessId
                || state.DwmStartTimeUtcTicks != committed.DwmStartTimeUtcTicks
                || !string.Equals(state.PatchPlanFingerprint, committed.PatchPlanFingerprint, StringComparison.Ordinal)
                || !string.Equals(state.DwmCoreVersion, committed.DwmCoreVersion, StringComparison.Ordinal))
                throw new InvalidDataException("Der gespeicherte DWM-Zustand passt nicht zum bestätigten Apply-Commit.");
            state.Settings.Validate();
            DwmShaderRewriter.ValidateSupportedSettings(state.Settings);
            if (!state.Settings.ApplySdrGammaCorrection)
                throw new InvalidDataException("Die SDR-Gammakorrektur ist nicht aktiviert.");
            if (approvedWindowsEnvironment == null || currentWindowsEnvironment == null
                || !approvedWindowsEnvironment.Matches(currentWindowsEnvironment))
                throw new InvalidDataException("Windows oder DWM stimmt nicht mit der freigegebenen Gamma-Aktivierung überein.");
            ProgramGammaBuildSupport.RequireSupported(committed.DwmCoreVersion, approvedWindowsEnvironment.DwmCoreSha256);
        }

        public static long ToCreationFileTime(long utcTicks)
        {
            try
            {
                var result = new DateTime(utcTicks, DateTimeKind.Utc).ToFileTimeUtc();
                if (result <= 0) throw new ArgumentOutOfRangeException(nameof(utcTicks));
                return result;
            }
            catch (ArgumentOutOfRangeException ex)
            {
                throw new InvalidDataException("Die DWM-Prozessstartzeit ist ungültig.", ex);
            }
        }

        public bool MatchesCurrentState(PersistedDwmState state, CalibrationSettings currentSettings,
            int currentSessionId)
        {
            try
            {
                return state != null && state.WasApplied && state.Settings != null
                    && state.Settings.ApplySdrGammaCorrection && currentSettings != null
                    && currentSettings.ApplySdrGammaCorrection
                    && state.DwmProcessId == DwmProcessId
                    && state.DwmStartTimeUtcTicks > 0 && state.DwmStartTimeUtcTicks == DwmStartTimeUtcTicks
                    && state.SessionId == SessionId && currentSessionId == SessionId
                    && string.Equals(state.DwmCoreVersion, DwmCoreVersion, StringComparison.Ordinal)
                    && string.Equals(state.PatchPlanFingerprint, PlanFingerprint, StringComparison.Ordinal)
                    && string.Equals(CalibrationSettingsIdentity.Compute(state.Settings), SettingsKey, StringComparison.Ordinal)
                    && string.Equals(CalibrationSettingsIdentity.Compute(currentSettings), SettingsKey, StringComparison.Ordinal);
            }
            catch (ArgumentException) { return false; }
        }

        public bool IsStillValid()
        {
            if (Volatile.Read(ref _revoked) != 0) return false;
            try
            {
                if (_isStillValid() && Volatile.Read(ref _revoked) == 0) return true;
            }
            catch { /* Lost read access is not continuing authorization. */ }
            Revoke();
            return false;
        }

        internal void Revoke() => Interlocked.Exchange(ref _revoked, 1);
    }

    /// <summary>
    /// Cheap update signals for lease renewal. A new activation still verifies full
    /// Windows hashes; a heartbeat reads only build identity and component metadata.
    /// Any observed change invalidates the existing approval permanently.
    /// </summary>
    internal sealed class ProgramGammaEnvironmentStamp
    {
        private readonly WindowsEnvironmentIdentity _environment;
        private readonly string _executablePath;
        private readonly string _corePath;
        private readonly string _executableStamp;
        private readonly string _coreStamp;

        private ProgramGammaEnvironmentStamp(WindowsEnvironmentIdentity environment)
        {
            _environment = environment.Clone();
            var directory = Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Sysnative")
                : Environment.SystemDirectory;
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidDataException("Das Windows-Systemverzeichnis ist nicht lesbar.");
            _executablePath = Path.Combine(directory, "dwm.exe");
            _corePath = Path.Combine(directory, "dwmcore.dll");
            _executableStamp = FileStamp(_executablePath);
            _coreStamp = FileStamp(_corePath);
        }

        public static ProgramGammaEnvironmentStamp Capture(WindowsEnvironmentIdentity environment)
        {
            if (environment == null) throw new ArgumentNullException(nameof(environment));
            environment.Validate();
            var result = new ProgramGammaEnvironmentStamp(environment);
            if (!result.IsCurrent()) throw new InvalidDataException("Windows wurde seit der Gamma-Freigabe geändert.");
            return result;
        }

        public bool IsCurrent()
        {
            using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", false))
            {
                if (key == null) return false;
                var build = key.GetValue("CurrentBuildNumber", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                var revision = key.GetValue("UBR", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                var buildLab = key.GetValue("BuildLabEx", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                int buildNumber;
                return int.TryParse(build, NumberStyles.None, CultureInfo.InvariantCulture, out buildNumber)
                    && buildNumber == _environment.OsBuildNumber
                    && revision is int && (int)revision == _environment.UpdateBuildRevision
                    && string.Equals(buildLab, _environment.BuildLabEx, StringComparison.Ordinal)
                    && string.Equals(FileStamp(_executablePath), _executableStamp, StringComparison.Ordinal)
                    && string.Equals(FileStamp(_corePath), _coreStamp, StringComparison.Ordinal);
            }
        }

        private static string FileStamp(string path)
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length <= 0)
                throw new InvalidDataException("Eine Windows-DWM-Komponente ist nicht lesbar.");
            return file.Length.ToString(CultureInfo.InvariantCulture) + ":"
                + file.CreationTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) + ":"
                + file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
        }
    }
}
