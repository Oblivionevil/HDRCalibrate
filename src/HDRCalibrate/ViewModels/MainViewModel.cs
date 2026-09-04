using HDRCalibrate.Core;
using HDRCalibrate.Dwm;
using HDRCalibrate.Services;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
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
        private DisplayInfo _selectedDisplay;
        private PersistedDwmState _dwmState;
        private DwmPatchResponse _preflight;
        private string _startupStateError;
        private string _statusMessage = "Bereit";
        private string _dwmStatus = "DWM wird geprüft";
        private bool _isBusy;
        private bool _isError;
        private bool _isDwmPatchActive;
        private bool _hasLegacyRestorePoint;
        private string _lastTestedSettingsKey;
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
        {
            RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
            ApplyCommand = new AsyncRelayCommand(ApplyAsync, () => CanApply && !IsBusy);
            TestCommand = new AsyncRelayCommand(RunTimedTestAsync, () => CanTest && !IsBusy);
            ResetProfileCommand = new AsyncRelayCommand(ResetCorrectionAsync, () => !IsBusy);
            NeutralCommand = new RelayCommand(SetNeutral, () => !IsBusy);
            SetGammaPresetCommand = new ParameterRelayCommand(SetGammaPreset, parameter => !IsBusy && parameter != null);
            OpenWindowsHdrCommand = new RelayCommand(OpenWindowsHdrSettings);

            try
            {
                _dwmState = _dwmStateStore.Load();
                if (_dwmState?.Settings != null) LoadSettings(_dwmState.Settings);
            }
            catch (Exception ex)
            {
                _startupStateError = ex.Message;
            }
            RebuildCurve();
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
        public bool CanApply
        {
            get
            {
                var settings = BuildSettings();
                return CanUseDwm && !IsLivePatchBlocked
                    && (settings.IsNeutral() || IsCurrentSettingsTested);
            }
        }
        public bool CanTest => CanUseDwm
            && _preflight?.TimedTestAvailable == true
            && !BuildSettings().IsNeutral();
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

        public async Task InitializeAsync()
        {
            await RefreshAsync();
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
                Displays.Clear();
                foreach (var display in detected) Displays.Add(display);
                SelectedDisplay = Displays.FirstOrDefault(item => item.Key == previousKey)
                               ?? Displays.FirstOrDefault(item => item.HdrActive)
                               ?? Displays.FirstOrDefault();

                _dwmState = _dwmStateStore.Load();
                _hasLegacyRestorePoint = detected.Any(display => _profileService.GetState(display) != null);
                UpdateDwmStatus();

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
            var display = SelectedDisplay;
            if (display == null) return;
            var settings = BuildSettings();
            settings.Validate();

            var action = settings.IsNeutral() ? "entfernt" : "neu geladen";
            var confirmation = MessageBox.Show(
                "Für die SDR-Korrektur wird der Desktop Window Manager einmal " + action + ". " +
                "Der Bildschirm kann dabei kurz schwarz werden. Es werden ausschließlich 16 freigegebene Konstanten " +
                "in den vier originalen SDR→scRGB-Funktionen geändert; Container, ABI, Aon9-Linkage, native HDR-Shader " +
                "und Anwendungen bleiben unverändert. Fortfahren?",
                "DWM-SDR-Korrektur anwenden",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (confirmation != MessageBoxResult.Yes) return;

            IsBusy = true;
            IsError = false;
            StatusMessage = "Alte HDR-Calibrate-Profile werden zurückgenommen …";
            try
            {
                var restoredProfiles = await Task.Run(RestoreLegacyProfiles);
                StatusMessage = settings.IsNeutral()
                    ? "DWM wird ungepatcht neu gestartet …"
                    : "Originalshader und 16 Konstanten werden geprüft; Administratorfreigabe wird benötigt …";

                var response = settings.IsNeutral()
                    ? await Task.Run(() => _dwmPatchService.Reset())
                    : await Task.Run(() => _dwmPatchService.Apply(settings));

                if (settings.IsNeutral())
                {
                    _dwmStateStore.Clear();
                    _dwmState = null;
                }
                else
                {
                    var newState = new PersistedDwmState
                    {
                        WasApplied = true,
                        DwmProcessId = response.DwmProcessId,
                        DwmStartTimeUtcTicks = response.DwmStartTimeUtcTicks,
                        SessionId = Process.GetCurrentProcess().SessionId,
                        DwmCoreVersion = response.DwmCoreVersion,
                        AppliedUtc = DateTime.UtcNow,
                        Settings = settings.Clone()
                    };
                    try
                    {
                        _dwmStateStore.Save(newState);
                        _dwmState = newState;
                    }
                    catch (Exception stateError)
                    {
                        try
                        {
                            await Task.Run(() => _dwmPatchService.ResetForRecovery());
                        }
                        catch (Exception rollbackError)
                        {
                            throw new AggregateException(
                                "Der DWM-Patch war aktiv, der Zustandsmerker konnte aber nicht gespeichert werden und auch der Sicherheits-Neustart schlug fehl.",
                                stateError,
                                rollbackError);
                        }
                        throw new InvalidOperationException(
                            "Der Zustandsmerker konnte nicht gespeichert werden. DWM wurde deshalb sofort wieder ungepatcht gestartet.",
                            stateError);
                    }
                }

                await RefreshAsync();
                StatusMessage = settings.IsNeutral()
                    ? "DWM-SDR-Korrektur entfernt" + FormatLegacyRestore(restoredProfiles) + "."
                    : response.PatchedConstantVectorCount + " SDR-Konstanten in PID " + response.DwmProcessId +
                      " aktiv; Originalshader und native HDR-Pfade unverändert" + FormatLegacyRestore(restoredProfiles) + ".";
            }
            catch (Exception ex)
            {
                SetError("Anwenden fehlgeschlagen: " + FlattenException(ex));
            }
            finally
            {
                IsBusy = false;
                RaiseDisplayProperties();
            }
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

            IsBusy = true;
            IsError = false;
            StatusMessage = "DWM wird zuerst ungepatcht neu gestartet …";
            try
            {
                var response = await Task.Run(() => _dwmPatchService.ResetForRecovery());
                _dwmStateStore.Clear();
                _dwmState = null;
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

            IsBusy = true;
            IsError = false;
            _lastTestedSettingsKey = null;
            OnPropertyChanged(nameof(IsCurrentSettingsTested));
            StatusMessage = "Alte HDR-Calibrate-Profile werden zurückgenommen …";
            try
            {
                var restoredProfiles = await Task.Run(RestoreLegacyProfiles);
                StatusMessage = "5-Sekunden-Test läuft; unabhängiger Watchdog ist vor dem Patch bereits aktiv …";
                var response = await Task.Run(() => _dwmPatchService.RunTimedTest(settings));

                _dwmStateStore.Clear();
                _dwmState = null;
                _lastTestedSettingsKey = GetSettingsKey(settings);
                OnPropertyChanged(nameof(IsCurrentSettingsTested));
                await RefreshAsync();
                StatusMessage = response.PatchedConstantVectorCount + " SDR-Konstanten wurden " +
                    (response.TestDurationMilliseconds / 1000.0).ToString("0.0", CultureInfo.CurrentCulture) +
                    " Sekunden getestet; Watchdog-Reset und ungepatchter DWM PID " + response.DwmProcessId +
                    " sind bestätigt. „Anwenden“ ist für genau diese Werte freigeschaltet" +
                    FormatLegacyRestore(restoredProfiles) + ".";
            }
            catch (Exception ex)
            {
                SetError("5-Sekunden-Test fehlgeschlagen: " + FlattenException(ex));
            }
            finally
            {
                IsBusy = false;
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
                DwmStatus = "DWM SDR-PFAD KOMPATIBEL · 16 KONSTANTEN";
            }
        }

        private static string GetSettingsKey(CalibrationSettings settings)
        {
            return string.Join("|", new[]
            {
                settings.ApplySdrGammaCorrection ? "1" : "0",
                settings.Gamma.ToString("R", CultureInfo.InvariantCulture),
                settings.Brightness.ToString("R", CultureInfo.InvariantCulture)
            });
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
