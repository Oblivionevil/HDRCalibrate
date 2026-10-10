using HDRCalibrate.Core;
using HDRCalibrate.Dwm;
using HDRCalibrate.Services;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace HDRCalibrate.ViewModels
{
    public sealed class MainViewModel : INotifyPropertyChanged
    {
        private readonly DisplayService _displayService = new DisplayService();
        private readonly ProfileService _profileService = new ProfileService();
        private readonly DwmPatchService _dwmPatchService = new DwmPatchService();
        private readonly DwmStateStore _dwmStateStore = new DwmStateStore();
        private readonly AutostartService _autostartService;
        private readonly IStartupActivationPolicyStore _startupActivationPolicyStore;
        private readonly IWindowsEnvironmentIdentityProvider _windowsEnvironmentIdentityProvider;
        private readonly Func<TimeSpan> _restartCooldownRemainingProvider;
        private readonly SnapshotWindowsEnvironmentProvider _startupEnvironmentSnapshot = new SnapshotWindowsEnvironmentProvider();
        private DisplayInfo _selectedDisplay;
        private PersistedDwmState _dwmState;
        private PersistedStartupActivationPolicy _startupActivationPolicy;
        private DwmPatchResponse _preflight;
        private string _preflightSettingsFingerprint;
        private string _startupStateError;
        private string _startupPolicyError;
        private bool _startupApprovalInvalidated;
        private bool _refreshingStartupActivationState;
        private Task _initializationTask;
        private string _statusMessage = "Bereit";
        private string _dwmStatus = "DWM wird geprüft";
        private bool _isBusy;
        private bool _isError;
        private bool _isDwmPatchActive;
        private bool _hasLegacyRestorePoint;
        private bool _isAutostartEnabled;
        private string _autostartStatus = "Autostart wird geprüft";
        private bool _isStartupActivationEnabled;
        private string _startupActivationStatus = "Startaktivierung wird geprüft";
        private string _lastTestedSettingsKey;
        private WindowsEnvironmentIdentity _lastTestedWindowsEnvironment;
        private ProgramGammaApproval _programGammaApproval;
        private string _programGammaApprovalFailure;
        private TimeSpan _restartCooldownRemaining;
        private string _restartCooldownError;
        private double _gamma = 2.2;
        private bool _applySdrGammaCorrection = true;
        private double _brightnessPercent;
        private double _contrast = 1.0;
        private double _saturationPercent = 100.0;
        private double _redGainPercent = 100.0;
        private double _greenGainPercent = 100.0;
        private double _blueGainPercent = 100.0;
        private PointCollection _curvePoints;

        public MainViewModel()
            : this(new AutostartService(), new StartupActivationPolicyStore())
        {
        }

        public MainViewModel(AutostartService autostartService)
            : this(autostartService, new StartupActivationPolicyStore())
        {
        }

        public MainViewModel(
            AutostartService autostartService,
            IStartupActivationPolicyStore startupActivationPolicyStore,
            IWindowsEnvironmentIdentityProvider windowsEnvironmentIdentityProvider = null,
            Func<TimeSpan> restartCooldownRemainingProvider = null)
        {
            _autostartService = autostartService ?? throw new ArgumentNullException(nameof(autostartService));
            _startupActivationPolicyStore = startupActivationPolicyStore
                ?? throw new ArgumentNullException(nameof(startupActivationPolicyStore));
            _windowsEnvironmentIdentityProvider = windowsEnvironmentIdentityProvider
                ?? new WindowsEnvironmentIdentityProvider();
            _restartCooldownRemainingProvider = restartCooldownRemainingProvider
                ?? _dwmPatchService.GetRestartCooldownRemaining;
            RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
            ApplyCommand = new AsyncRelayCommand(ApplyAsync, () => CanApply && !IsBusy);
            TestCommand = new AsyncRelayCommand(RunTimedTestAsync, () => CanTest && !IsBusy);
            ResetProfileCommand = new AsyncRelayCommand(ResetCorrectionAsync, () => !IsBusy);
            NeutralCommand = new RelayCommand(SetNeutral, () => !IsBusy);
            SetGammaPresetCommand = new ParameterRelayCommand(SetGammaPreset, parameter => !IsBusy && parameter != null);
            OpenWindowsHdrCommand = new RelayCommand(OpenWindowsHdrSettings);
            ToggleAutostartCommand = new RelayCommand(ToggleAutostart);
            ToggleStartupActivationCommand = new RelayCommand(
                ToggleStartupActivation,
                () => CanToggleStartupActivation);

            try
            {
                _dwmState = _dwmStateStore.Load();
                if (_dwmState?.Settings != null) LoadSettings(_dwmState.Settings);
            }
            catch (Exception ex)
            {
                _startupStateError = ex.Message;
            }
            try
            {
                _startupActivationPolicy = _startupActivationPolicyStore.Load();
            }
            catch (Exception ex)
            {
                _startupPolicyError = ex.Message;
            }
            RefreshAutostartState();
            RefreshStartupActivationState();
            RebuildCurve();
            RefreshRestartCooldown();
        }

        public ObservableCollection<DisplayInfo> Displays { get; } = new ObservableCollection<DisplayInfo>();

        public DisplayInfo SelectedDisplay
        {
            get => _selectedDisplay;
            set
            {
                if (!Set(ref _selectedDisplay, value)) return;
                RaiseDisplayProperties();
                RebuildCurve();
            }
        }

        public double Gamma
        {
            get => _gamma;
            set
            {
                if (!Set(ref _gamma, value)) return;
                if (!_applySdrGammaCorrection)
                {
                    _applySdrGammaCorrection = true;
                    OnPropertyChanged(nameof(ApplySdrGammaCorrection));
                }
                RebuildCurve();
            }
        }

        public bool ApplySdrGammaCorrection
        {
            get => _applySdrGammaCorrection;
            set
            {
                if (!Set(ref _applySdrGammaCorrection, value)) return;
                if (!value && Math.Abs(_brightnessPercent) > 0.000001)
                {
                    _brightnessPercent = 0.0;
                    OnPropertyChanged(nameof(BrightnessPercent));
                }
                RebuildCurve();
            }
        }

        public double BrightnessPercent
        {
            get => _brightnessPercent;
            set
            {
                if (!Set(ref _brightnessPercent, value)) return;
                if (Math.Abs(value) > 0.000001 && !_applySdrGammaCorrection)
                {
                    _applySdrGammaCorrection = true;
                    OnPropertyChanged(nameof(ApplySdrGammaCorrection));
                }
                RebuildCurve();
            }
        }

        public double Contrast
        {
            get => _contrast;
            set { if (Set(ref _contrast, value)) RebuildCurve(); }
        }

        public double SaturationPercent
        {
            get => _saturationPercent;
            set { if (Set(ref _saturationPercent, value)) RebuildCurve(); }
        }

        public double RedGainPercent
        {
            get => _redGainPercent;
            set { if (Set(ref _redGainPercent, value)) RebuildCurve(); }
        }

        public double GreenGainPercent
        {
            get => _greenGainPercent;
            set { if (Set(ref _greenGainPercent, value)) RebuildCurve(); }
        }

        public double BlueGainPercent
        {
            get => _blueGainPercent;
            set { if (Set(ref _blueGainPercent, value)) RebuildCurve(); }
        }

        public PointCollection CurvePoints
        {
            get => _curvePoints;
            private set => Set(ref _curvePoints, value);
        }

        public string StatusMessage
        {
            get => _statusMessage;
            private set => Set(ref _statusMessage, value);
        }

        public string DwmStatus
        {
            get => _dwmStatus;
            private set => Set(ref _dwmStatus, value);
        }

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (!Set(ref _isBusy, value)) return;
                RaiseCommandStates();
            }
        }

        public bool IsError
        {
            get => _isError;
            private set => Set(ref _isError, value);
        }

        public bool IsAutostartEnabled
        {
            get => _isAutostartEnabled;
            private set => Set(ref _isAutostartEnabled, value);
        }

        public string AutostartStatus
        {
            get => _autostartStatus;
            private set => Set(ref _autostartStatus, value);
        }

        public bool IsStartupActivationEnabled
        {
            get => _isStartupActivationEnabled;
            private set => Set(ref _isStartupActivationEnabled, value);
        }

        public string StartupActivationStatus
        {
            get => _startupActivationStatus;
            private set => Set(ref _startupActivationStatus, value);
        }

        public bool HasDisplay => SelectedDisplay != null;
        public bool IsHdrActive => SelectedDisplay?.HdrActive == true;
        public bool IsDwmCompatible => _preflight?.Success == true;
        public bool IsLivePatchBlocked => _preflight?.LivePatchBlocked == true;
        public bool IsDwmPatchActive => _isDwmPatchActive;
        private bool CanUseDwm => IsHdrActive
            && IsDwmCompatible
            && !(SelectedDisplay?.AdvancedColorLimitedByPolicy ?? true);
        public bool IsCurrentSettingsTested => string.Equals(
            _lastTestedSettingsKey, GetSettingsKey(BuildSettings()), StringComparison.Ordinal);
        public bool IsRestartCooldownActive => _restartCooldownRemaining > TimeSpan.Zero
            || !string.IsNullOrEmpty(_restartCooldownError);
        public string ApplyButtonText => !string.IsNullOrEmpty(_restartCooldownError)
            ? "Neustart gesperrt"
            : _restartCooldownRemaining > TimeSpan.Zero
                ? "Warten (" + Math.Ceiling(_restartCooldownRemaining.TotalSeconds).ToString("0", CultureInfo.CurrentCulture) + " s)"
                : "Anwenden";
        public string RestartCooldownText => !string.IsNullOrEmpty(_restartCooldownError)
            ? "Neustartsperre nicht prüfbar: " + _restartCooldownError
            : _restartCooldownRemaining > TimeSpan.Zero
                ? "Anzeige-Schutz: noch " + Math.Ceiling(_restartCooldownRemaining.TotalSeconds).ToString("0", CultureInfo.CurrentCulture)
                    + " s warten." + (IsCurrentSettingsTested ? " Der erfolgreiche Test bleibt gültig." : string.Empty)
                : string.Empty;
        public bool CanApply
        {
            get
            {
                var settings = BuildSettings();
                return CanUseDwm && !IsLivePatchBlocked && !IsRestartCooldownActive
                    && (settings.IsNeutral() || IsCurrentSettingsTested);
            }
        }
        public bool CanTest => CanUseDwm
            && !IsRestartCooldownActive
            && _preflight?.TimedTestAvailable == true
            && !BuildSettings().IsNeutral();
        public bool CanToggleStartupActivation => !IsBusy
            && (IsStartupActivationEnabled
            || (IsAutostartEnabled
                && StartupActivationPolicy.CanEnable(
                    _startupActivationPolicy,
                    GetAppVersion(),
                    GetHelperVersionOrEmpty(),
                    _preflight,
                    _preflightSettingsFingerprint,
                    _dwmState,
                    DwmPatchService.IsAppliedStateCurrent(_dwmState),
                    _startupEnvironmentSnapshot)));
        public bool HasRestorePoint => _dwmState != null || _hasLegacyRestorePoint;
        public string DisplayStatus => SelectedDisplay == null
            ? "Kein Monitor ausgewählt"
            : IsHdrActive ? "HDR AKTIV" : SelectedDisplay.HdrUserEnabled ? "HDR NICHT AKTIV" : "HDR AUS";
        public string ActiveProfile => SelectedDisplay?.ProfileLabel ?? "–";
        public string BitDepth => SelectedDisplay?.BitDepthLabel ?? "–";
        public string SdrWhite => SelectedDisplay?.SdrWhiteLabel ?? "–";
        public string PeakLuminance => SelectedDisplay?.ColorMetadata == null
            ? "–"
            : $"{SelectedDisplay.ColorMetadata.MaxLuminanceNits:0} nit";

        public ICommand RefreshCommand { get; }
        public ICommand ApplyCommand { get; }
        public ICommand TestCommand { get; }
        public ICommand ResetProfileCommand { get; }
        public ICommand NeutralCommand { get; }
        public ICommand SetGammaPresetCommand { get; }
        public ICommand OpenWindowsHdrCommand { get; }
        public ICommand ToggleAutostartCommand { get; }
        public ICommand ToggleStartupActivationCommand { get; }

        public void RefreshRestartCooldown()
        {
            var previousSeconds = Math.Ceiling(_restartCooldownRemaining.TotalSeconds);
            var previousError = _restartCooldownError;
            try
            {
                var remaining = _restartCooldownRemainingProvider();
                _restartCooldownRemaining = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
                _restartCooldownError = null;
            }
            catch (Exception ex)
            {
                _restartCooldownRemaining = TimeSpan.Zero;
                _restartCooldownError = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
            }
            if (previousSeconds == Math.Ceiling(_restartCooldownRemaining.TotalSeconds)
                && string.Equals(previousError, _restartCooldownError, StringComparison.Ordinal)) return;
            OnPropertyChanged(nameof(IsRestartCooldownActive));
            OnPropertyChanged(nameof(RestartCooldownText));
            OnPropertyChanged(nameof(ApplyButtonText));
            OnPropertyChanged(nameof(CanApply));
            OnPropertyChanged(nameof(CanTest));
            (ApplyCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (TestCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }

        public ProgramGammaApproval GetProgramGammaApproval()
        {
            if (Volatile.Read(ref _isBusy))
                throw new InvalidOperationException("Die globale SDR-Korrektur wird gerade geprüft oder geändert. Bitte den Vorgang zuerst abschließen.");
            var knownVersion = _preflight?.DwmCoreVersion ?? _dwmState?.DwmCoreVersion;
            if (!string.IsNullOrWhiteSpace(knownVersion) && !ProgramGammaBuildSupport.IsSupportedVersion(knownVersion))
                throw new NotSupportedException(ProgramGammaBuildSupport.UnsupportedMessage);
            var approval = Volatile.Read(ref _programGammaApproval);
            if (approval == null)
            {
                var failure = Volatile.Read(ref _programGammaApprovalFailure);
                throw new InvalidOperationException(failure != null
                    ? "Die Gamma-Freigabe für Programme konnte nach dem letzten Anwenden nicht erstellt werden: "
                        + failure + " Bitte die globale SDR-Korrektur erneut testen und anwenden."
                    : "Die SDR-Gammakorrektur muss in dieser App-Sitzung erfolgreich getestet und anschließend angewendet werden. Ein gespeicherter Zustand allein genügt nicht.");
            }
            try
            {
                // Activation performs the full hash check. Lease heartbeats use the
                // cheap update stamp captured alongside this committed snapshot.
                var currentEnvironment = _windowsEnvironmentIdentityProvider.Capture();
                if (!approval.WindowsEnvironment.Matches(currentEnvironment)
                    || !approval.IsStillValid())
                    throw new InvalidDataException("Windows, DWM oder die bestätigten Gammaeinstellungen wurden geändert.");
                return approval;
            }
            catch (Exception ex)
            {
                InvalidateProgramGammaApproval();
                throw new InvalidOperationException("Die Gamma-Freigabe für Programme ist nicht mehr gültig. Bitte die globale SDR-Korrektur erneut testen und anwenden.", ex);
            }
        }

        private void EstablishProgramGammaApproval(DwmPatchResponse committed,
            PersistedDwmState state, WindowsEnvironmentIdentity approvedEnvironment)
        {
            InvalidateProgramGammaApproval();
            if (state?.Settings?.ApplySdrGammaCorrection != true) return;
            // A successful global commit remains successful even when the
            // separately versioned private native hook is unavailable.
            if (committed == null || approvedEnvironment == null
                || !ProgramGammaBuildSupport.IsSupported(committed.DwmCoreVersion, approvedEnvironment.DwmCoreSha256))
                return;
            try
            {
                var stamp = ProgramGammaEnvironmentStamp.Capture(approvedEnvironment);
                var currentEnvironment = _windowsEnvironmentIdentityProvider.Capture();
                ProgramGammaApproval approval = null;
                approval = ProgramGammaApproval.CreateFromCommittedApply(committed, state,
                    approvedEnvironment, currentEnvironment,
                    () => IsProgramGammaApprovalCurrent(approval, stamp));
                if (!stamp.IsCurrent())
                    throw new InvalidDataException("Windows wurde während der Gamma-Freigabe geändert.");
                Volatile.Write(ref _programGammaApproval, approval);
            }
            catch (Exception ex)
            {
                // The optional per-program approval never decides the outcome of
                // an already committed global apply or startup activation.
                InvalidateProgramGammaApproval();
                Volatile.Write(ref _programGammaApprovalFailure,
                    string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message);
                Trace.TraceWarning("Gamma-Freigabe für Programme nicht erstellt: " + ex);
            }
        }

        private bool IsProgramGammaApprovalCurrent(ProgramGammaApproval approval,
            ProgramGammaEnvironmentStamp stamp)
        {
            if (approval == null || Volatile.Read(ref _isBusy)
                || !ReferenceEquals(Volatile.Read(ref _programGammaApproval), approval)
                || !CanUseDwm || IsLivePatchBlocked)
                return false;
            using (var currentProcess = Process.GetCurrentProcess())
            {
                if (!approval.MatchesCurrentState(_dwmState, BuildSettings(), currentProcess.SessionId)
                    || DwmPatchService.GetCurrentSessionDwmProcessId() != approval.DwmProcessId)
                    return false;
            }
            using (var dwm = Process.GetProcessById(approval.DwmProcessId))
            {
                return !dwm.HasExited && dwm.SessionId == approval.SessionId
                    && string.Equals(dwm.ProcessName, "dwm", StringComparison.OrdinalIgnoreCase)
                    && dwm.StartTime.ToUniversalTime().Ticks == approval.DwmStartTimeUtcTicks
                    && stamp.IsCurrent()
                    && ReferenceEquals(Volatile.Read(ref _programGammaApproval), approval);
            }
        }

        private void InvalidateProgramGammaApproval()
        {
            Volatile.Write(ref _programGammaApprovalFailure, null);
            Interlocked.Exchange(ref _programGammaApproval, null)?.Revoke();
        }

        public void RefreshAutostartState()
        {
            try
            {
                IsAutostartEnabled = _autostartService.IsEnabled();
                AutostartStatus = !IsAutostartEnabled
                    ? "Aus"
                    : _autostartService.NeedsMigration()
                        ? "Aktiv · alter Startbefehl (wird beim nächsten Aktivieren aktualisiert)"
                        : "Aktiv · startet verborgen im Infobereich";
            }
            catch (Exception ex)
            {
                IsAutostartEnabled = false;
                AutostartStatus = "Status nicht verfügbar: " + ex.Message;
            }
            RefreshStartupActivationState();
        }

        public void ToggleAutostart()
        {
            try
            {
                var enable = !IsAutostartEnabled;
                _autostartService.SetEnabled(enable);
                RefreshAutostartState();
                IsError = false;
                StatusMessage = enable
                    ? "Autostart aktiviert: HDR Calibrate startet bei der Anmeldung verborgen im Infobereich."
                    : "Autostart deaktiviert.";
                RefreshStartupActivationState();
            }
            catch (Exception ex)
            {
                RefreshAutostartState();
                SetError("Autostart konnte nicht geändert werden: " + ex.Message);
            }
        }

        public void RefreshStartupActivationState()
        {
            if (_refreshingStartupActivationState) return;
            _refreshingStartupActivationState = true;
            try
            {
                _startupActivationPolicy = _startupActivationPolicyStore.Load();
                _startupEnvironmentSnapshot.Identity = null;
                if (_startupActivationPolicy != null)
                {
                    if (_startupActivationPolicy.ApprovalRevoked && !_startupActivationPolicy.Enabled)
                    {
                        InvalidateSessionTest();
                        _startupApprovalInvalidated = true;
                        _startupPolicyError = WindowsApprovalRevokedMessage;
                    }
                    else
                    {
                        try { _startupEnvironmentSnapshot.Identity = _windowsEnvironmentIdentityProvider.Capture(); }
                        catch { /* An unavailable identity must invalidate, never approve, the saved test. */ }
                        if (!StartupActivationPolicy.IsValidForCurrentBuild(
                            _startupActivationPolicy, GetAppVersion(), GetHelperVersionOrEmpty(),
                            _startupEnvironmentSnapshot))
                            RevokeStartupApproval(WindowsApprovalRevokedMessage);
                    }
                }
                IsStartupActivationEnabled = _startupActivationPolicy?.Enabled == true;
                if (!string.IsNullOrWhiteSpace(_startupPolicyError))
                {
                    StartupActivationStatus = "Nicht verfügbar: " + _startupPolicyError;
                }
                else if (IsStartupActivationEnabled && !IsAutostartEnabled)
                {
                    StartupActivationStatus = "Pausiert · Windows-Autostart ist aus";
                }
                else if (IsStartupActivationEnabled)
                {
                    StartupActivationStatus = "Aktiv · geprüfte SDR-Werte · UAC beim Windows-Start";
                }
                else if (CanEnableStartupActivation())
                {
                    StartupActivationStatus = "Bereit · kann für die geprüften aktiven Werte eingeschaltet werden";
                }
                else
                {
                    StartupActivationStatus = "Aus · zuerst 5 Sekunden testen und anschließend anwenden";
                }
            }
            catch (Exception ex)
            {
                _startupActivationPolicy = null;
                RevokeStartupApproval("Der gespeicherte Testnachweis ist nicht lesbar. Erneute Freigabe erforderlich: " + ex.Message);
                StartupActivationStatus = "Gesperrt: " + _startupPolicyError;
            }
            finally
            {
                _refreshingStartupActivationState = false;
            }
            OnPropertyChanged(nameof(CanToggleStartupActivation));
            (ToggleStartupActivationCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        public void ToggleStartupActivation()
        {
            if (IsBusy) return;
            RefreshStartupActivationState();
            var enable = !IsStartupActivationEnabled;
            if (enable)
            {
                if (!CanEnableStartupActivation())
                {
                    SetError(
                        "Startaktivierung ist erst verfügbar, nachdem exakt diese Werte fünf Sekunden getestet und anschließend angewendet wurden.");
                    return;
                }
                var confirmation = MessageBox.Show(
                    "Beim nächsten Windows-Start wird die exakt getestete SDR-Korrektur automatisch angefordert. " +
                    "DWM wird dabei einmal sicher neu gestartet; der Bildschirm kann kurz schwarz werden und Windows zeigt eine UAC-Abfrage. " +
                    "Ein unabhängiger Watchdog setzt DWM ohne bestätigten Zustands-Commit ungepatcht zurück. Aktivieren?",
                    "SDR-Korrektur beim Windows-Start aktivieren",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);
                if (confirmation != MessageBoxResult.Yes) return;
            }

            try
            {
                if (_startupActivationPolicy == null)
                    throw new InvalidOperationException("Es liegt kein erfolgreicher überwachter Testnachweis vor.");
                var previous = _startupActivationPolicy.Enabled;
                var disableRecovered = false;
                _startupActivationPolicy.Enabled = enable;
                try
                {
                    if (enable) _autostartService.SetEnabled(true);
                    _startupActivationPolicyStore.Save(_startupActivationPolicy);
                    _startupPolicyError = null;
                }
                catch (Exception saveError)
                {
                    _startupActivationPolicy.Enabled = previous;
                    if (enable) throw;

                    DisableStartupActivationFailClosed();
                    if (IsStartupActivationEnabled)
                        throw new InvalidOperationException(
                            _startupPolicyError ?? "Startaktivierung konnte nicht sicher deaktiviert werden.",
                            saveError);
                    disableRecovered = true;
                }
                RefreshAutostartState();
                RefreshStartupActivationState();
                IsError = false;
                StatusMessage = enable
                    ? "Startaktivierung eingeschaltet: Nur die exakt geprüften SDR-Werte werden beim Windows-Start nach UAC-Freigabe angewendet."
                    : disableRecovered
                        ? "Startaktivierung ausgeschaltet; der nicht aktualisierbare Testnachweis wurde sicher entfernt."
                        : "Startaktivierung ausgeschaltet; die aktuell laufende DWM-Korrektur bleibt unverändert.";
            }
            catch (Exception ex)
            {
                _startupPolicyError = ex.Message;
                RefreshStartupActivationState();
                SetError("Startaktivierung konnte nicht geändert werden: " + ex.Message);
            }
        }

        public void ReportExitDeferred()
        {
            IsError = false;
            StatusMessage = "Beenden wurde angefordert. Der laufende DWM-Vorgang wird zuerst sicher abgeschlossen.";
        }

        public async Task<StartupActivationResult> TryActivateAtWindowsStartupAsync(bool isStartupLaunch)
        {
            await InitializeAsync();
            IsBusy = true;
            try
            {
                RefreshStartupActivationState();
                if (isStartupLaunch && _startupApprovalInvalidated)
                {
                    SetError(_startupPolicyError ?? WindowsApprovalRevokedMessage);
                    return new StartupActivationResult(StartupActivationDecision.ApprovalInvalid, false, false, StatusMessage);
                }

                CalibrationSettings approvedSettings = null;
                if (isStartupLaunch && _startupActivationPolicy?.Enabled == true)
                {
                    try
                    {
                        StartupActivationPolicy.ValidateStoredPolicy(_startupActivationPolicy, true);
                        approvedSettings = _startupActivationPolicy.Settings.Clone();
                        var approvedFingerprint = GetSettingsKey(approvedSettings);
                        try
                        {
                            _preflight = await Task.Run(() => _dwmPatchService.Preflight(approvedSettings));
                        }
                        catch
                        {
                            _preflight = null;
                        }
                        _preflightSettingsFingerprint = approvedFingerprint;
                    }
                    catch
                    {
                        approvedSettings = null;
                        _preflight = null;
                        _preflightSettingsFingerprint = null;
                    }
                }

                var decision = StartupActivationPolicy.Evaluate(
                    _startupActivationPolicy,
                    isStartupLaunch,
                    GetAppVersion(),
                    GetHelperVersionOrEmpty(),
                    _preflight,
                    _preflightSettingsFingerprint,
                    Displays.Any(item => item.HdrActive),
                    SelectedDisplay?.AdvancedColorLimitedByPolicy ?? true,
                    _dwmState,
                    DwmPatchService.IsAppliedStateCurrent(_dwmState),
                    _windowsEnvironmentIdentityProvider);

                if (decision == StartupActivationDecision.Disabled
                    || decision == StartupActivationDecision.NotStartupLaunch)
                    return new StartupActivationResult(decision, false, true, string.Empty);
                if (decision == StartupActivationDecision.AlreadyActive)
                {
                    StatusMessage = "Die geprüfte SDR-Korrektur ist im aktuellen DWM bereits aktiv; kein Neustart und keine UAC-Abfrage erforderlich.";
                    return new StartupActivationResult(decision, false, true, StatusMessage);
                }
                if (decision != StartupActivationDecision.Ready)
                {
                    var message = GetStartupDecisionMessage(decision);
                    if (decision == StartupActivationDecision.ApprovalInvalid)
                    {
                        RevokeStartupApproval(WindowsApprovalRevokedMessage);
                        if (IsStartupActivationEnabled && IsAutostartEnabled)
                            message += " Die automatische Wiederholung konnte nicht deaktiviert werden: " +
                                (_startupPolicyError ?? "unbekannter Speicherfehler");
                    }
                    SetError(message);
                    return new StartupActivationResult(decision, false, false, message);
                }

                approvedSettings = approvedSettings ?? _startupActivationPolicy.Settings.Clone();
                InvalidateProgramGammaApproval();
                var approvedPolicy = _startupActivationPolicy;
                var approvedWindowsEnvironment = approvedPolicy.WindowsEnvironment.Clone();
                LoadSettings(approvedSettings);
                RaiseCalibrationSettingProperties();
                RebuildCurve();
                IsError = false;
                StatusMessage = "Geprüfte SDR-Startkorrektur wird überwacht aktiviert; Administratorfreigabe wird benötigt …";
                try
                {
                    var startupApproval = CaptureStartupApprovalFile(approvedPolicy);
                    var restoredProfiles = await Task.Run(RestoreLegacyProfiles);
                    PersistedDwmState committedState = null;
                    var response = await Task.Run(() => _dwmPatchService.ApplySupervised(
                        approvedSettings,
                        activation =>
                        {
                            if (_startupApprovalInvalidated)
                                throw new InvalidDataException("Die Startfreigabe wurde während der Aktivierung widerrufen.");
                            committedState = CreateAppliedState(activation, approvedSettings);
                            _dwmStateStore.Save(committedState);
                        },
                        approvedWindowsEnvironment,
                        startupApproval));
                    _dwmState = committedState
                        ?? throw new InvalidDataException("Der überwachte Start-Commit enthält keinen DWM-Zustand.");
                    EstablishProgramGammaApproval(response, committedState, approvedWindowsEnvironment);
                    await RefreshAsync();
                    StatusMessage = response.PatchedConstantVectorCount +
                        " geprüfte SDR-Konstanten beim Windows-Start in PID " + response.DwmProcessId +
                        " aktiviert; Watchdog-Commit bestätigt" + FormatLegacyRestore(restoredProfiles) + ".";
                    RefreshStartupActivationState();
                    return new StartupActivationResult(
                        StartupActivationDecision.Ready,
                        true,
                        true,
                        StatusMessage);
                }
                catch (Exception ex)
                {
                    RevokeStartupApproval("Die Startaktivierung wurde nicht sicher bestätigt. Neuer Test und erneute Freigabe erforderlich.");
                    try
                    {
                        _dwmState = _dwmStateStore.Load();
                        UpdateDwmStatus();
                    }
                    catch
                    {
                        _dwmState = null;
                    }
                    var startupRetryBlocked = !IsStartupActivationEnabled || !IsAutostartEnabled;
                    var message = startupRetryBlocked
                        ? "Startaktivierung fehlgeschlagen und für weitere Starts deaktiviert: " + FlattenException(ex)
                        : "Startaktivierung fehlgeschlagen; die automatische Wiederholung konnte nicht deaktiviert werden: " +
                            FlattenException(ex) + " | " +
                            (_startupPolicyError ?? "unbekannter Speicherfehler");
                    SetError(message);
                    return new StartupActivationResult(
                        StartupActivationDecision.Ready,
                        true,
                        false,
                        message);
                }
            }
            finally
            {
                IsBusy = false;
                RefreshRestartCooldown();
                RaiseDisplayProperties();
                RefreshStartupActivationState();
            }
        }

        public async Task InitializeAsync()
        {
            if (_initializationTask == null) _initializationTask = RefreshAsync();
            await _initializationTask;
        }

        private async Task RefreshAsync()
        {
            var previousKey = SelectedDisplay?.Key;
            var settings = BuildSettings();
            IsBusy = true;
            IsError = false;
            StatusMessage = "Monitore und DWM-SDR-Pipeline werden geprüft …";
            try
            {
                var displaysTask = Task.Run(() => _displayService.GetActiveDisplays());
                var preflightTask = Task.Run(() => _dwmPatchService.Preflight(settings));
                await Task.WhenAll(displaysTask, preflightTask);

                var detected = displaysTask.Result;
                _preflight = preflightTask.Result;
                _preflightSettingsFingerprint = GetSettingsKey(settings);
                Displays.Clear();
                foreach (var display in detected) Displays.Add(display);
                SelectedDisplay = Displays.FirstOrDefault(item => item.Key == previousKey)
                               ?? Displays.FirstOrDefault(item => item.HdrActive)
                               ?? Displays.FirstOrDefault();

                _dwmState = _dwmStateStore.Load();
                _hasLegacyRestorePoint = detected.Any(display => _profileService.GetState(display) != null);
                UpdateDwmStatus();
                RefreshStartupActivationState();

                if (!string.IsNullOrWhiteSpace(_startupStateError))
                {
                    var error = _startupStateError;
                    _startupStateError = null;
                    SetError(error);
                }
                else if (_preflight.LivePatchBlocked && !_preflight.TimedTestAvailable)
                {
                    SetError(_preflight.Message);
                }
                else if (!_preflight.Success)
                {
                    SetError("DWM-Prüfung fehlgeschlagen: " + _preflight.Message);
                }
                else
                {
                    StatusMessage = detected.Count == 0
                        ? "Windows meldet keinen aktiven Monitor."
                        : $"{detected.Count} aktiver Monitor erkannt; DWM-SDR-Pfad statisch verifiziert.";
                }
            }
            catch (Exception ex)
            {
                InvalidateProgramGammaApproval();
                SetError("Erkennung fehlgeschlagen: " + ex.Message);
            }
            finally
            {
                IsBusy = false;
                RaiseDisplayProperties();
            }
        }

        private async Task ApplyAsync()
        {
            // Recheck before confirmation or any side effects. A countdown is not
            // authorization; the service independently enforces the same guard.
            RefreshRestartCooldown();
            if (IsRestartCooldownActive)
            {
                IsError = false;
                StatusMessage = RestartCooldownText;
                return;
            }
            var display = SelectedDisplay;
            if (display == null) return;
            var settings = BuildSettings();
            settings.Validate();

            var action = settings.IsNeutral() ? "entfernt" : "neu geladen";
            var confirmation = MessageBox.Show(
                "Für die SDR-Korrektur wird der Desktop Window Manager einmal " + action + ". " +
                "Der Bildschirm kann dabei kurz schwarz werden. Es werden ausschließlich 24 freigegebene Konstanten " +
                "in den sechs originalen SDR→scRGB-Funktionen geändert; Container, ABI, Aon9-Linkage, native HDR-Shader " +
                "und Anwendungen bleiben unverändert. Fortfahren?",
                "DWM-SDR-Korrektur anwenden",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (confirmation != MessageBoxResult.Yes) return;
            // The dialog may stay open for any length of time. Recheck before the
            // program approval or legacy profiles are touched.
            RefreshRestartCooldown();
            if (IsRestartCooldownActive)
            {
                IsError = false;
                StatusMessage = RestartCooldownText;
                return;
            }

            InvalidateProgramGammaApproval();
            IsBusy = true;
            IsError = false;
            StatusMessage = "Alte HDR-Calibrate-Profile werden zurückgenommen …";
            try
            {
                var restoredProfiles = await Task.Run(RestoreLegacyProfiles);
                StatusMessage = settings.IsNeutral()
                    ? "DWM wird ungepatcht neu gestartet …"
                    : "Originalshader und 24 Konstanten werden geprüft; Administratorfreigabe wird benötigt …";

                PersistedDwmState committedState = null;
                var response = settings.IsNeutral()
                    ? await Task.Run(() => _dwmPatchService.Reset())
                    : await Task.Run(() => _dwmPatchService.ApplySupervised(
                        settings,
                        activation =>
                        {
                            committedState = CreateAppliedState(activation, settings);
                            _dwmStateStore.Save(committedState);
                        },
                        _lastTestedWindowsEnvironment));

                if (settings.IsNeutral())
                {
                    _dwmStateStore.Clear();
                    _dwmState = null;
                    ClearStartupActivationPolicy();
                }
                else
                {
                    _dwmState = committedState
                        ?? throw new InvalidDataException("Der überwachte Apply-Commit enthält keinen DWM-Zustand.");
                    EstablishProgramGammaApproval(response, committedState, _lastTestedWindowsEnvironment);
                }

                await RefreshAsync();
                StatusMessage = settings.IsNeutral()
                    ? "DWM-SDR-Korrektur entfernt" + FormatLegacyRestore(restoredProfiles) + "."
                    : response.PatchedConstantVectorCount + " SDR-Konstanten in PID " + response.DwmProcessId +
                      " aktiv; Watchdog-Commit bestätigt, Originalshader und native HDR-Pfade unverändert" +
                      FormatLegacyRestore(restoredProfiles) + ".";
            }
            catch (Exception ex)
            {
                HandleApplyFailure(ex);
            }
            finally
            {
                IsBusy = false;
                RefreshRestartCooldown();
                RaiseDisplayProperties();
            }
        }

        private void HandleApplyFailure(Exception ex)
        {
            if (ex is DwmRestartCooldownException)
            {
                // The service rejects before creating a request or starting a
                // helper, so DWM was not touched and the successful test stays
                // valid. Legacy profiles may already be restored and the program
                // approval was revoked; the next successful Apply re-establishes it.
                RefreshRestartCooldown();
                IsError = false;
                StatusMessage = IsRestartCooldownActive ? RestartCooldownText
                    : IsCurrentSettingsTested
                        ? "Schutzpause beendet. Der erfolgreiche Test bleibt gültig; bitte erneut Anwenden wählen."
                        : "Schutzpause beendet; bitte erneut Anwenden wählen.";
                return;
            }
            try
            {
                _dwmState = _dwmStateStore.Load();
                UpdateDwmStatus();
            }
            catch
            {
                _dwmState = null;
            }
            RevokeStartupApproval("Anwenden wurde nicht sicher bestätigt. Neuer Test und erneute Freigabe erforderlich.");
            SetError("Anwenden fehlgeschlagen: " + FlattenException(ex));
        }

        private async Task ResetCorrectionAsync()
        {
            var confirmation = MessageBox.Show(
                "DWM wird als Notfall-Wiederherstellung ungepatcht neu gestartet. " +
                "Der Bildschirm kann kurz schwarz werden. Fortfahren?",
                "Notfall-Reset: DWM ungepatcht neu starten",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (confirmation != MessageBoxResult.Yes) return;

            InvalidateProgramGammaApproval();
            IsBusy = true;
            IsError = false;
            StatusMessage = "DWM wird zuerst ungepatcht neu gestartet …";
            try
            {
                var response = await Task.Run(() => _dwmPatchService.ResetForRecovery());
                _dwmStateStore.Clear();
                _dwmState = null;
                ClearStartupActivationPolicy();
                StatusMessage = "DWM läuft ungepatcht; alte Profilreste werden bereinigt …";
                var restoredProfiles = await Task.Run(RestoreLegacyProfiles);
                SetNeutralValues();
                await RefreshAsync();
                StatusMessage = "Notfall-Reset abgeschlossen: DWM läuft ungepatcht in PID " +
                    response.DwmProcessId + FormatLegacyRestore(restoredProfiles) + ".";
            }
            catch (Exception ex)
            {
                SetError("Zurücksetzen fehlgeschlagen: " + FlattenException(ex));
            }
            finally
            {
                IsBusy = false;
                RaiseDisplayProperties();
            }
        }

        private async Task RunTimedTestAsync()
        {
            RefreshRestartCooldown();
            if (IsRestartCooldownActive)
            {
                IsError = false;
                StatusMessage = RestartCooldownText;
                return;
            }
            var display = SelectedDisplay;
            if (display == null) return;
            var settings = BuildSettings();
            settings.Validate();
            if (settings.IsNeutral())
            {
                SetError("Für den 5-Sekunden-Test muss mindestens eine SDR-Korrektur aktiv sein.");
                return;
            }

            var removesExistingPatch = DwmPatchService.IsAppliedStateCurrent(_dwmState);
            var confirmation = MessageBox.Show(
                "Die aktuellen Regler werden fünf Sekunden lang auf DWM-komponierte SDR-Inhalte angewendet. " +
                "DWM wird zum Start und danach automatisch ungepatcht neu gestartet; der Bildschirm kann jeweils kurz schwarz werden. " +
                (removesExistingPatch
                    ? "Die derzeit aktive DWM-Korrektur wird dabei entfernt und anschließend nicht wiederhergestellt. "
                    : string.Empty) +
                "Der erhöhte Helfer führt den Reset auch aus, wenn dieses Fenster während des Tests geschlossen wird. Fortfahren?",
                "SDR-Korrektur 5 Sekunden testen",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (confirmation != MessageBoxResult.Yes) return;
            // Like Apply: recheck the guard before the previous proof, the startup
            // approval or legacy profiles are touched. The service enforces it again.
            RefreshRestartCooldown();
            if (IsRestartCooldownActive)
            {
                IsError = false;
                StatusMessage = RestartCooldownText;
                return;
            }

            IsBusy = true;
            IsError = false;
            InvalidateSessionTest();
            RevokeStartupApproval("Ein neuer Test ersetzt den bisherigen Nachweis. Bis zum erfolgreichen Abschluss bleibt die Startaktivierung widerrufen.");
            OnPropertyChanged(nameof(IsCurrentSettingsTested));
            StatusMessage = "Alte HDR-Calibrate-Profile werden zurückgenommen …";
            try
            {
                var testedWindowsEnvironment = await Task.Run(() => _windowsEnvironmentIdentityProvider.Capture());
                var restoredProfiles = await Task.Run(RestoreLegacyProfiles);
                StatusMessage = "5-Sekunden-Test läuft; unabhängiger Watchdog ist vor dem Patch bereits aktiv …";
                var response = await Task.Run(() => _dwmPatchService.RunTimedTest(settings));
                if (!testedWindowsEnvironment.Matches(await Task.Run(() => _windowsEnvironmentIdentityProvider.Capture())))
                    throw new InvalidDataException("Windows oder DWM wurde während des Tests geändert. Der Test wird nicht freigegeben.");

                string startupPolicyWarning = null;
                try
                {
                    _startupActivationPolicy = StartupActivationPolicy.CreateFromSuccessfulTest(
                        settings,
                        response,
                        GetAppVersion(),
                        GetHelperVersionOrEmpty(),
                        false,
                        testedWindowsEnvironment,
                        _windowsEnvironmentIdentityProvider);
                    _startupActivationPolicyStore.Save(_startupActivationPolicy);
                    _startupPolicyError = null;
                    _startupApprovalInvalidated = false;
                }
                catch (Exception policyError)
                {
                    _startupActivationPolicy = null;
                    _startupPolicyError = policyError.Message;
                    startupPolicyWarning = " Startaktivierung bleibt gesperrt: " + policyError.Message;
                }

                _dwmStateStore.Clear();
                _dwmState = null;
                _lastTestedSettingsKey = GetSettingsKey(settings);
                _lastTestedWindowsEnvironment = testedWindowsEnvironment.Clone();
                OnPropertyChanged(nameof(IsCurrentSettingsTested));
                await RefreshAsync();
                StatusMessage = response.PatchedConstantVectorCount + " SDR-Konstanten wurden " +
                    (response.TestDurationMilliseconds / 1000.0).ToString("0.0", CultureInfo.CurrentCulture) +
                    " Sekunden getestet; Watchdog-Reset und ungepatchter DWM PID " + response.DwmProcessId +
                    " sind bestätigt. „Anwenden“ ist für genau diese Werte freigeschaltet" +
                    FormatLegacyRestore(restoredProfiles) + "." + startupPolicyWarning;
                RefreshStartupActivationState();
            }
            catch (Exception ex)
            {
                RevokeStartupApproval("Der Test wurde nicht sicher bestätigt. Neuer Test und erneute Freigabe erforderlich.");
                SetError("5-Sekunden-Test fehlgeschlagen: " + FlattenException(ex));
            }
            finally
            {
                IsBusy = false;
                RefreshRestartCooldown();
                RaiseDisplayProperties();
            }
        }

        private int RestoreLegacyProfiles()
        {
            var restored = 0;
            foreach (var display in _displayService.GetActiveDisplays())
            {
                if (_profileService.GetState(display) == null) continue;
                _profileService.Reset(display);
                restored++;
            }
            return restored;
        }

        private CalibrationSettings BuildSettings()
        {
            return new CalibrationSettings
            {
                Gamma = Gamma,
                ApplySdrGammaCorrection = ApplySdrGammaCorrection,
                Brightness = BrightnessPercent / 100.0,
                Contrast = 1.0,
                Saturation = 1.0,
                RedGain = 1.0,
                GreenGain = 1.0,
                BlueGain = 1.0
            };
        }

        private void LoadSettings(CalibrationSettings settings)
        {
            settings.Validate();
            InvalidateProgramGammaApproval();
            _gamma = settings.Gamma;
            _applySdrGammaCorrection = settings.ApplySdrGammaCorrection;
            _brightnessPercent = settings.Brightness * 100.0;
            _contrast = 1.0;
            _saturationPercent = 100.0;
            _redGainPercent = 100.0;
            _greenGainPercent = 100.0;
            _blueGainPercent = 100.0;
        }

        private void SetNeutral()
        {
            SetNeutralValues();
            IsError = false;
            StatusMessage = "Regler neutralisiert. DWM kann über „Anwenden“ oder „DWM-Korrektur entfernen“ ungepatcht neu gestartet werden.";
        }

        private void SetNeutralValues()
        {
            Gamma = 2.2;
            ApplySdrGammaCorrection = false;
            BrightnessPercent = 0.0;
            Contrast = 1.0;
            SaturationPercent = 100.0;
            RedGainPercent = 100.0;
            GreenGainPercent = 100.0;
            BlueGainPercent = 100.0;
        }

        private void SetGammaPreset(object parameter)
        {
            if (parameter == null) return;
            if (!double.TryParse(Convert.ToString(parameter, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var gamma))
                return;
            Gamma = gamma;
            ApplySdrGammaCorrection = true;
            IsError = false;
            StatusMessage = $"SDR-Zielgamma {gamma:0.0} gewählt. Bitte zuerst fünf Sekunden testen.";
        }

        private static void OpenWindowsHdrSettings()
        {
            Process.Start(new ProcessStartInfo("ms-settings:display") { UseShellExecute = true });
        }

        private void RebuildCurve()
        {
            var points = new PointCollection();
            var settings = BuildSettings();
            var approval = Volatile.Read(ref _programGammaApproval);
            if (approval != null
                && !string.Equals(approval.SettingsKey, GetSettingsKey(settings), StringComparison.Ordinal))
                InvalidateProgramGammaApproval();
            const double width = 360.0;
            const double height = 112.0;
            for (var index = 0; index <= 90; index++)
            {
                var input = index / 90.0;
                var output = SdrAdjustmentMath.EvaluateGray(settings, input);
                points.Add(new Point(input * width, height - output * height));
            }
            CurvePoints = points;
            OnPropertyChanged(nameof(IsCurrentSettingsTested));
            OnPropertyChanged(nameof(CanApply));
            OnPropertyChanged(nameof(CanTest));
            (ApplyCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (TestCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }

        private void UpdateDwmStatus()
        {
            Set(ref _isDwmPatchActive, DwmPatchService.IsAppliedStateCurrent(_dwmState), nameof(IsDwmPatchActive));
            var approval = Volatile.Read(ref _programGammaApproval);
            if (approval != null)
            {
                using (var currentProcess = Process.GetCurrentProcess())
                    if (!_isDwmPatchActive || !CanUseDwm || IsLivePatchBlocked
                        || !approval.MatchesCurrentState(_dwmState, BuildSettings(), currentProcess.SessionId))
                        InvalidateProgramGammaApproval();
            }
            if (_preflight?.LivePatchBlocked == true && _preflight.TimedTestAvailable)
            {
                DwmStatus = "ÜBERWACHTER TEST BEREIT · ANWENDEN GESPERRT";
            }
            else if (_preflight?.LivePatchBlocked == true)
            {
                DwmStatus = "DWM-LIVEPATCH SICHERHEITSSPERRE";
            }
            else if (_preflight?.Success != true)
            {
                DwmStatus = "DWM NICHT KOMPATIBEL";
            }
            else if (IsDwmPatchActive)
            {
                DwmStatus = "DWM SDR-PATCH AKTIV · PID " + _dwmState.DwmProcessId;
            }
            else if (_dwmState != null)
            {
                DwmStatus = "DWM SDR-PATCH NICHT MEHR AKTIV";
            }
            else
            {
                DwmStatus = "DWM SDR-PFAD KOMPATIBEL · 24 KONSTANTEN";
            }
        }

        private static string GetSettingsKey(CalibrationSettings settings)
        {
            return CalibrationSettingsIdentity.Compute(settings);
        }

        private bool CanEnableStartupActivation()
        {
            return IsAutostartEnabled
                && StartupActivationPolicy.CanEnable(
                    _startupActivationPolicy,
                    GetAppVersion(),
                    GetHelperVersionOrEmpty(),
                    _preflight,
                    _preflightSettingsFingerprint,
                    _dwmState,
                    DwmPatchService.IsAppliedStateCurrent(_dwmState),
                    _startupEnvironmentSnapshot);
        }

        private static PersistedDwmState CreateAppliedState(
            DwmPatchResponse response,
            CalibrationSettings settings)
        {
            if (response == null) throw new ArgumentNullException(nameof(response));
            if (!response.Success
                || response.DwmProcessId <= 0
                || response.DwmStartTimeUtcTicks <= 0
                || string.IsNullOrWhiteSpace(response.PatchPlanFingerprint))
                throw new InvalidDataException("Der aktive DWM-Zustand ist unvollständig.");
            return new PersistedDwmState
            {
                WasApplied = true,
                DwmProcessId = response.DwmProcessId,
                DwmStartTimeUtcTicks = response.DwmStartTimeUtcTicks,
                SessionId = Process.GetCurrentProcess().SessionId,
                DwmCoreVersion = response.DwmCoreVersion,
                AppliedUtc = DateTime.UtcNow,
                PatchPlanFingerprint = response.PatchPlanFingerprint,
                Settings = settings.Clone()
            };
        }

        private StartupApprovalFileProof CaptureStartupApprovalFile(PersistedStartupActivationPolicy expected)
        {
            var store = _startupActivationPolicyStore as StartupActivationPolicyStore;
            if (store == null) throw new InvalidDataException("Die Startfreigabe ist nicht als überprüfbare Datei gespeichert.");
            using (var lease = new FileStream(store.PolicyPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var current = store.Load();
                StartupActivationPolicy.ValidateStoredPolicy(current, true);
                if (_startupApprovalInvalidated
                    || !StartupActivationPolicy.IsValidForCurrentBuild(
                        current, GetAppVersion(), GetHelperVersionOrEmpty(), _windowsEnvironmentIdentityProvider)
                    || !current.WindowsEnvironment.Matches(expected.WindowsEnvironment)
                    || current.TestedUtc != expected.TestedUtc
                    || !string.Equals(current.SettingsFingerprint, expected.SettingsFingerprint, StringComparison.Ordinal)
                    || !string.Equals(current.PatchPlanFingerprint, expected.PatchPlanFingerprint, StringComparison.Ordinal))
                    throw new InvalidDataException("Die ursprüngliche Startfreigabe ist nicht mehr gültig.");
                // Holding the read lease prevents replacement between validation and hashing.
                return StartupApprovalFileProof.Capture(store.PolicyPath);
            }
        }

        private const string WindowsApprovalRevokedMessage =
            "Freigabe widerrufen: Windows-Build, Update-Revision, DWM-Dateien oder Programmstand wurden geändert " +
            "oder konnten nicht sicher geprüft werden. Erneut 5 Sekunden testen, anwenden und die Startaktivierung ausdrücklich einschalten.";

        private sealed class SnapshotWindowsEnvironmentProvider : IWindowsEnvironmentIdentityProvider
        {
            public WindowsEnvironmentIdentity Identity { get; set; }
            public WindowsEnvironmentIdentity Capture()
            {
                if (Identity == null) throw new InvalidDataException("Die aktuelle Windows-Identität ist nicht bestätigt.");
                return Identity.Clone();
            }
        }

        private void InvalidateSessionTest()
        {
            InvalidateProgramGammaApproval();
            _lastTestedSettingsKey = null;
            _lastTestedWindowsEnvironment = null;
            OnPropertyChanged(nameof(IsCurrentSettingsTested));
            OnPropertyChanged(nameof(CanApply));
            (ApplyCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }

        private void RevokeStartupApproval(string reason)
        {
            InvalidateSessionTest();
            _startupApprovalInvalidated = true;
            _startupPolicyError = reason;
            var policy = _startupActivationPolicy;
            var wasEnabled = policy?.Enabled == true;
            var wasRevoked = policy?.ApprovalRevoked == true;
            if (policy != null)
            {
                try
                {
                    // A durable revocation cannot become valid again after an OS rollback.
                    policy.Enabled = false;
                    policy.ApprovalRevoked = true;
                    _startupActivationPolicyStore.Save(policy);
                    IsStartupActivationEnabled = false;
                    return;
                }
                catch (Exception saveError)
                {
                    policy.Enabled = wasEnabled;
                    policy.ApprovalRevoked = wasRevoked;
                    _startupPolicyError += " | Widerruf konnte nicht gespeichert werden: " + saveError.Message;
                }
            }
            try
            {
                // Includes legacy records that cannot be saved with the current schema.
                _startupActivationPolicyStore.Clear();
                _startupActivationPolicy = null;
                IsStartupActivationEnabled = false;
                return;
            }
            catch (Exception clearError)
            {
                _startupPolicyError += " | Alter Nachweis konnte nicht entfernt werden: " + clearError.Message;
            }

            IsStartupActivationEnabled = wasEnabled;
            try
            {
                _autostartService.SetEnabled(false);
                IsAutostartEnabled = _autostartService.IsEnabled();
                AutostartStatus = "Aus · Windows-Update-Sicherheitssperre";
            }
            catch (Exception autostartError)
            {
                _startupPolicyError += " | Auch Windows-Autostart konnte nicht deaktiviert werden: " + autostartError.Message;
            }
        }

        private void DisableStartupActivationFailClosed()
        {
            if (_startupActivationPolicy == null || !_startupActivationPolicy.Enabled) return;
            var policy = _startupActivationPolicy;
            try
            {
                policy.Enabled = false;
                _startupActivationPolicyStore.Save(policy);
                _startupPolicyError = null;
                IsStartupActivationEnabled = false;
                return;
            }
            catch (Exception saveError)
            {
                policy.Enabled = true;
                try
                {
                    _startupActivationPolicyStore.Clear();
                    _startupActivationPolicy = null;
                    _startupPolicyError = null;
                    IsStartupActivationEnabled = false;
                    return;
                }
                catch (Exception clearError)
                {
                    _startupPolicyError =
                        "Startaktivierung konnte weder deaktiviert noch entfernt werden; Windows-Autostart wird als Rückfallebene ausgeschaltet. " +
                        saveError.Message + " | " + clearError.Message;
                }
            }

            IsStartupActivationEnabled = true;
            try
            {
                _autostartService.SetEnabled(false);
                RefreshAutostartState();
            }
            catch (Exception autostartError)
            {
                _startupPolicyError += " | Auch Windows-Autostart konnte nicht ausgeschaltet werden: " +
                    autostartError.Message;
            }
        }

        private void ClearStartupActivationPolicy()
        {
            try
            {
                _startupActivationPolicyStore.Clear();
                _startupActivationPolicy = null;
                _startupPolicyError = null;
                IsStartupActivationEnabled = false;
                RefreshStartupActivationState();
            }
            catch (Exception clearError)
            {
                DisableStartupActivationFailClosed();
                if (IsStartupActivationEnabled && IsAutostartEnabled)
                    throw new InvalidOperationException(
                        "Der Testnachweis konnte nach dem DWM-Reset nicht entfernt und seine automatische Wiederholung nicht deaktiviert werden.",
                        clearError);
            }
        }

        private static string GetStartupDecisionMessage(StartupActivationDecision decision)
        {
            switch (decision)
            {
                case StartupActivationDecision.ApprovalInvalid:
                    return WindowsApprovalRevokedMessage;
                case StartupActivationDecision.HdrUnavailable:
                    return "Startaktivierung ausgelassen: Windows meldet derzeit keinen aktiven HDR-Monitor.";
                case StartupActivationDecision.DisplayRestricted:
                    return "Startaktivierung ausgelassen: Advanced Color ist für den ausgewählten Monitor durch Windows-Richtlinien eingeschränkt.";
                case StartupActivationDecision.DwmIncompatible:
                    return "Startaktivierung gesperrt: Die installierte DWM-Version oder das geprüfte Shadermanifest ist nicht kompatibel.";
                case StartupActivationDecision.LivePatchBlocked:
                    return "Startaktivierung gesperrt: Der sichere dauerhafte DWM-Pfad ist in diesem Build deaktiviert.";
                default:
                    return "Startaktivierung wurde aus Sicherheitsgründen nicht ausgeführt.";
            }
        }

        private static string GetAppVersion()
        {
            return typeof(MainViewModel).Assembly.GetName().Version.ToString();
        }

        private static string GetHelperVersionOrEmpty()
        {
            try
            {
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "HDRCalibrate.DwmHelper.exe");
                if (!File.Exists(path)) return string.Empty;
                return (FileVersionInfo.GetVersionInfo(path).FileVersion ?? string.Empty).Trim();
            }
            catch
            {
                return string.Empty;
            }
        }

        private void RaiseCalibrationSettingProperties()
        {
            OnPropertyChanged(nameof(Gamma));
            OnPropertyChanged(nameof(ApplySdrGammaCorrection));
            OnPropertyChanged(nameof(BrightnessPercent));
            OnPropertyChanged(nameof(Contrast));
            OnPropertyChanged(nameof(SaturationPercent));
            OnPropertyChanged(nameof(RedGainPercent));
            OnPropertyChanged(nameof(GreenGainPercent));
            OnPropertyChanged(nameof(BlueGainPercent));
        }

        private void RaiseDisplayProperties()
        {
            OnPropertyChanged(nameof(HasDisplay));
            OnPropertyChanged(nameof(IsHdrActive));
            OnPropertyChanged(nameof(IsDwmCompatible));
            OnPropertyChanged(nameof(IsLivePatchBlocked));
            OnPropertyChanged(nameof(IsDwmPatchActive));
            OnPropertyChanged(nameof(CanApply));
            OnPropertyChanged(nameof(CanTest));
            OnPropertyChanged(nameof(CanToggleStartupActivation));
            OnPropertyChanged(nameof(HasRestorePoint));
            OnPropertyChanged(nameof(DisplayStatus));
            OnPropertyChanged(nameof(ActiveProfile));
            OnPropertyChanged(nameof(BitDepth));
            OnPropertyChanged(nameof(SdrWhite));
            OnPropertyChanged(nameof(PeakLuminance));
            RaiseCommandStates();
        }

        private void RaiseCommandStates()
        {
            (RefreshCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ApplyCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (TestCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (ResetProfileCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (NeutralCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SetGammaPresetCommand as ParameterRelayCommand)?.RaiseCanExecuteChanged();
            (ToggleStartupActivationCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private static string FormatLegacyRestore(int count)
        {
            return count <= 0 ? string.Empty : "; " + count + " alte MHC2-Zuordnung bereinigt";
        }

        private static string FlattenException(Exception exception)
        {
            if (!(exception is AggregateException aggregate)) return exception.Message;
            return string.Join(" | ", aggregate.Flatten().InnerExceptions.Select(item => item.Message));
        }

        private void SetError(string message)
        {
            IsError = true;
            StatusMessage = message;
        }

        private bool Set<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
