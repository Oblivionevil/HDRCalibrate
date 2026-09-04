using HDRCalibrate.Core;
using HDRCalibrate.Dwm;
using System;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Serialization;

namespace HDRCalibrate.Services
{
    [Serializable]
    public sealed class PersistedStartupActivationPolicy
    {
        public int SchemaVersion { get; set; }
        public bool Enabled { get; set; }
        public bool ApprovalRevoked { get; set; }
        public string SettingsFingerprint { get; set; }
        public string DwmCoreVersion { get; set; }
        public string DwmCoreSha256 { get; set; }
        public string PatchPlanFingerprint { get; set; }
        public string PatchImplementationId { get; set; }
        public string AppVersion { get; set; }
        public string HelperVersion { get; set; }
        public WindowsEnvironmentIdentity WindowsEnvironment { get; set; }
        public DateTime TestedUtc { get; set; }
        public bool WatchdogResetConfirmed { get; set; }
        public CalibrationSettings Settings { get; set; } = new CalibrationSettings();
    }

    public interface IStartupActivationPolicyStore
    {
        PersistedStartupActivationPolicy Load();
        void Save(PersistedStartupActivationPolicy policy);
        void Clear();
    }

    public sealed class StartupActivationPolicyStore : IStartupActivationPolicyStore
    {
        private readonly string _path;
        private readonly XmlSerializer _serializer = new XmlSerializer(typeof(PersistedStartupActivationPolicy));

        public StartupActivationPolicyStore(string baseDirectory = null)
        {
            var root = baseDirectory ?? System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HDRCalibrate");
            _path = System.IO.Path.Combine(root, "dwm-startup-policy.xml");
        }

        public string PolicyPath => _path;

        public PersistedStartupActivationPolicy Load()
        {
            if (!File.Exists(_path)) return null;
            try
            {
                var info = new FileInfo(_path);
                if (info.Length <= 0 || info.Length > 64 * 1024)
                    throw new InvalidDataException("Die Startaktivierungsrichtlinie besitzt eine ungültige Größe.");
                using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = XmlReader.Create(stream, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null
                }))
                    return (PersistedStartupActivationPolicy)_serializer.Deserialize(reader);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidDataException("Die Startaktivierungsrichtlinie ist beschädigt.", ex);
            }
            catch (XmlException ex)
            {
                throw new InvalidDataException("Die Startaktivierungsrichtlinie ist beschädigt.", ex);
            }
        }

        public void Save(PersistedStartupActivationPolicy policy)
        {
            if (policy == null) throw new ArgumentNullException(nameof(policy));
            StartupActivationPolicy.ValidateStoredPolicy(policy, false);

            var directory = System.IO.Path.GetDirectoryName(_path);
            Directory.CreateDirectory(directory);
            var temporary = _path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                _serializer.Serialize(stream, policy);
                stream.Flush(true);
            }
            if (File.Exists(_path))
                File.Replace(temporary, _path, _path + ".bak", true);
            else
                File.Move(temporary, _path);
        }

        public void Clear()
        {
            foreach (var path in new[] { _path, _path + ".tmp", _path + ".bak" }.Distinct())
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }

    public enum StartupActivationDecision
    {
        Disabled,
        NotStartupLaunch,
        ApprovalInvalid,
        AlreadyActive,
        HdrUnavailable,
        DisplayRestricted,
        DwmIncompatible,
        LivePatchBlocked,
        Ready
    }

    public static class StartupActivationPolicy
    {
        public const int CurrentSchemaVersion = 2;

        public static PersistedStartupActivationPolicy CreateFromSuccessfulTest(
            CalibrationSettings settings,
            DwmPatchResponse testResponse,
            string appVersion,
            string helperVersion,
            bool preserveEnabled,
            WindowsEnvironmentIdentity testedWindowsEnvironment,
            IWindowsEnvironmentIdentityProvider windowsEnvironmentIdentityProvider = null)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (testResponse == null) throw new ArgumentNullException(nameof(testResponse));
            settings.Validate();
            if (settings.IsNeutral())
                throw new InvalidOperationException("Neutrale Einstellungen können nicht für den Windows-Start freigegeben werden.");
            if (!testResponse.Success
                || !testResponse.WatchdogArmed
                || testResponse.TestDurationMilliseconds < TimedDwmTestRunner.DurationMilliseconds
                || testResponse.TestDurationMilliseconds > TimedDwmTestRunner.DurationMilliseconds + 1000
                || testResponse.PatchActivatedUtcTicks <= DateTime.MinValue.Ticks
                || testResponse.WatchdogResetUtcTicks <= testResponse.PatchActivatedUtcTicks
                || testResponse.TestedDwmProcessId <= 0
                || testResponse.DwmProcessId <= 0
                || testResponse.DwmStartTimeUtcTicks <= 0
                || string.IsNullOrWhiteSpace(testResponse.PatchPlanFingerprint))
                throw new InvalidDataException("Der überwachte 5-Sekunden-Test enthält keinen vollständigen Sicherheitsnachweis.");

            if (testedWindowsEnvironment == null)
                throw new InvalidDataException("Die vor dem Test geprüfte Windows-Umgebung fehlt.");
            testedWindowsEnvironment = testedWindowsEnvironment.Clone();
            testedWindowsEnvironment.Validate();
            var currentWindowsEnvironment = CaptureCurrentWindowsEnvironment(windowsEnvironmentIdentityProvider);
            if (!testedWindowsEnvironment.Matches(currentWindowsEnvironment))
                throw new InvalidDataException("Windows oder seine DWM-Komponenten wurden während des Tests geändert. Bitte erneut testen.");
            if (!string.Equals(testedWindowsEnvironment.DwmCoreSha256,
                DwmCoreInspector.SupportedDwmCoreSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Die getestete Windows-DWM-Komponente ist nicht für diese Programmversion freigegeben.");

            var policy = new PersistedStartupActivationPolicy
            {
                SchemaVersion = CurrentSchemaVersion,
                Enabled = preserveEnabled,
                ApprovalRevoked = false,
                SettingsFingerprint = CalibrationSettingsIdentity.Compute(settings),
                DwmCoreVersion = testResponse.DwmCoreVersion,
                DwmCoreSha256 = testedWindowsEnvironment.DwmCoreSha256.ToUpperInvariant(),
                PatchPlanFingerprint = testResponse.PatchPlanFingerprint,
                PatchImplementationId = DwmPatchSafety.PatchImplementationId,
                AppVersion = RequireIdentity(appVersion, nameof(appVersion)),
                HelperVersion = RequireIdentity(helperVersion, nameof(helperVersion)),
                WindowsEnvironment = testedWindowsEnvironment,
                TestedUtc = DateTime.UtcNow,
                WatchdogResetConfirmed = true,
                Settings = settings.Clone()
            };
            ValidateStoredPolicy(policy, false);
            return policy;
        }

        public static StartupActivationDecision Evaluate(
            PersistedStartupActivationPolicy policy,
            bool isStartupLaunch,
            string appVersion,
            string helperVersion,
            DwmPatchResponse preflight,
            string preflightSettingsFingerprint,
            bool hasHdrDisplay,
            bool displayRestricted,
            PersistedDwmState activeState,
            bool isActiveStateCurrent,
            IWindowsEnvironmentIdentityProvider windowsEnvironmentIdentityProvider = null)
        {
            if (policy == null || !policy.Enabled) return StartupActivationDecision.Disabled;
            if (!isStartupLaunch) return StartupActivationDecision.NotStartupLaunch;
            if (!IsValidForCurrentBuild(policy, appVersion, helperVersion, windowsEnvironmentIdentityProvider))
                return StartupActivationDecision.ApprovalInvalid;
            if (isActiveStateCurrent && ActiveStateMatchesPolicy(activeState, policy))
                return StartupActivationDecision.AlreadyActive;
            if (!hasHdrDisplay) return StartupActivationDecision.HdrUnavailable;
            if (displayRestricted) return StartupActivationDecision.DisplayRestricted;
            if (preflight == null || !preflight.Success)
                return StartupActivationDecision.DwmIncompatible;
            if (preflight.LivePatchBlocked) return StartupActivationDecision.LivePatchBlocked;
            if (!PreflightMatchesPolicy(policy, preflight, preflightSettingsFingerprint))
                return StartupActivationDecision.ApprovalInvalid;
            return StartupActivationDecision.Ready;
        }

        public static bool CanEnable(
            PersistedStartupActivationPolicy policy,
            string appVersion,
            string helperVersion,
            DwmPatchResponse preflight,
            string preflightSettingsFingerprint,
            PersistedDwmState activeState,
            bool isActiveStateCurrent,
            IWindowsEnvironmentIdentityProvider windowsEnvironmentIdentityProvider = null)
        {
            return policy != null
                && IsValidForCurrentBuild(policy, appVersion, helperVersion, windowsEnvironmentIdentityProvider)
                && PreflightMatchesPolicy(policy, preflight, preflightSettingsFingerprint)
                && isActiveStateCurrent
                && ActiveStateMatchesPolicy(activeState, policy);
        }

        public static void ValidateStoredPolicy(
            PersistedStartupActivationPolicy policy,
            bool requireEnabled)
        {
            if (policy == null) throw new ArgumentNullException(nameof(policy));
            if (policy.SchemaVersion != CurrentSchemaVersion)
                throw new InvalidDataException("Der Testnachweis besitzt keine aktuelle Windows-Update-Sperre. Bitte erneut testen und Startaktivierung ausdrücklich freigeben.");
            if (requireEnabled && !policy.Enabled)
                throw new InvalidDataException("Die Startaktivierungsrichtlinie ist nicht aktiviert.");
            if (policy.Enabled && policy.ApprovalRevoked)
                throw new InvalidDataException("Der widerrufene Testnachweis darf nicht für den Windows-Start aktiviert sein.");
            if (policy.Settings == null) throw new InvalidDataException("Die geprüften Einstellungen fehlen.");
            policy.Settings.Validate();
            if (policy.Settings.IsNeutral()) throw new InvalidDataException("Die geprüften Einstellungen sind neutral.");
            if (!IsSha256(policy.SettingsFingerprint)
                || !string.Equals(
                    policy.SettingsFingerprint,
                    CalibrationSettingsIdentity.Compute(policy.Settings),
                    StringComparison.Ordinal))
                throw new InvalidDataException("Der Einstellungsfingerabdruck stimmt nicht überein.");
            if (!IsSha256(policy.DwmCoreSha256)
                || !IsSha256(policy.PatchPlanFingerprint))
                throw new InvalidDataException("Ein Sicherheitsfingerabdruck fehlt oder ist ungültig.");
            RequireIdentity(policy.DwmCoreVersion, nameof(policy.DwmCoreVersion));
            RequireIdentity(policy.PatchImplementationId, nameof(policy.PatchImplementationId));
            RequireIdentity(policy.AppVersion, nameof(policy.AppVersion));
            RequireIdentity(policy.HelperVersion, nameof(policy.HelperVersion));
            if (policy.WindowsEnvironment == null)
                throw new InvalidDataException("Die geprüfte Windows-Umgebung fehlt im Testnachweis.");
            policy.WindowsEnvironment.Validate();
            if (!string.Equals(policy.DwmCoreSha256, policy.WindowsEnvironment.DwmCoreSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Die geprüfte Windows-DWM-Komponente stimmt nicht mit dem Testnachweis überein.");
            if (!policy.WatchdogResetConfirmed)
                throw new InvalidDataException("Der überwachte Test-Reset wurde nicht bestätigt.");
            if (policy.TestedUtc.Kind != DateTimeKind.Utc
                || policy.TestedUtc <= new DateTime(621355968000000000L, DateTimeKind.Utc)
                || policy.TestedUtc > DateTime.UtcNow.AddMinutes(5))
                throw new InvalidDataException("Der Testzeitpunkt ist ungültig.");
        }

        public static bool IsValidForCurrentBuild(
            PersistedStartupActivationPolicy policy,
            string appVersion,
            string helperVersion,
            IWindowsEnvironmentIdentityProvider windowsEnvironmentIdentityProvider = null)
        {
            if (policy == null || policy.ApprovalRevoked) return false;
            try
            {
                ValidateStoredPolicy(policy, false);
                return string.Equals(policy.AppVersion, appVersion, StringComparison.Ordinal)
                    && string.Equals(policy.HelperVersion, helperVersion, StringComparison.Ordinal)
                    && string.Equals(policy.DwmCoreVersion, DwmCoreInspector.SupportedDwmCoreVersion, StringComparison.Ordinal)
                    && string.Equals(policy.DwmCoreSha256, DwmCoreInspector.SupportedDwmCoreSha256, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(policy.PatchImplementationId, DwmPatchSafety.PatchImplementationId, StringComparison.Ordinal)
                    && policy.WindowsEnvironment.Matches(CaptureCurrentWindowsEnvironment(windowsEnvironmentIdentityProvider));
            }
            catch
            {
                return false;
            }
        }

        private static WindowsEnvironmentIdentity CaptureCurrentWindowsEnvironment(
            IWindowsEnvironmentIdentityProvider windowsEnvironmentIdentityProvider)
        {
            var identity = (windowsEnvironmentIdentityProvider ?? new WindowsEnvironmentIdentityProvider()).Capture();
            if (identity == null)
                throw new InvalidDataException("Die aktuelle Windows-Umgebung konnte nicht geprüft werden.");
            identity.Validate();
            return identity;
        }

        private static bool PreflightMatchesPolicy(
            PersistedStartupActivationPolicy policy,
            DwmPatchResponse preflight,
            string preflightSettingsFingerprint)
        {
            return policy != null
                && preflight != null
                && preflight.Success
                && IsSha256(preflightSettingsFingerprint)
                && string.Equals(
                    preflightSettingsFingerprint,
                    policy.SettingsFingerprint,
                    StringComparison.Ordinal)
                && string.Equals(preflight.DwmCoreVersion, policy.DwmCoreVersion, StringComparison.Ordinal)
                && string.Equals(
                    preflight.PatchPlanFingerprint,
                    policy.PatchPlanFingerprint,
                    StringComparison.Ordinal);
        }

        private static bool ActiveStateMatchesPolicy(
            PersistedDwmState state,
            PersistedStartupActivationPolicy policy)
        {
            return state != null
                && state.Settings != null
                && string.Equals(
                    CalibrationSettingsIdentity.Compute(state.Settings),
                    policy.SettingsFingerprint,
                    StringComparison.Ordinal)
                && string.Equals(
                    state.PatchPlanFingerprint,
                    policy.PatchPlanFingerprint,
                    StringComparison.Ordinal);
        }

        private static string RequireIdentity(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 256)
                throw new InvalidDataException("Die Identität " + name + " fehlt oder ist ungültig.");
            return value;
        }

        private static bool IsSha256(string value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && value.Length == 64
                && value.All(character =>
                    (character >= '0' && character <= '9')
                    || (character >= 'A' && character <= 'F'));
        }
    }
}
