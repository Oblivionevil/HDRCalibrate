using HDRCalibrate.Services;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace HDRCalibrate
{
    public sealed class ProgramProfileRow
    {
        public ProgramLaunchProfile Profile { get; }
        public string Name { get; }
        public string Path => Profile.ExecutablePath;
        public string ModeLabel => IsSupported ? ProgramProfileRules.Label(Profile.ColorMode) : "Kein Browser-Startprofil";
        public string GammaLabel => Profile.ForceSdrGamma ? "SDR-Gamma: erlaubt · manuell aktivieren" : "SDR-Gamma: nicht freigegeben";
        public string Status { get; }
        public bool IsSupported { get; }
        public bool IsExecutableAvailable { get; }

        public ProgramProfileRow(ProgramLaunchProfile profile)
        {
            Profile = profile.Copy();
            var info = ProgramExecutableInspector.Inspect(profile.ExecutablePath);
            Name = info.Name;
            Status = info.Status;
            IsSupported = info.IsSupported;
            IsExecutableAvailable = File.Exists(Profile.ExecutablePath);
        }
    }

    public sealed class ProgramColorChoice
    {
        public ProgramColorMode Mode { get; set; }
        public string Label => ProgramProfileRules.Label(Mode);
    }

    public partial class ProgramProfilesWindow : Window
    {
        private readonly ProgramProfileStore _store;
        private readonly ProgramProfileLauncher _launcher;
        private readonly IProgramGammaController _gammaController;
        private readonly bool _gammaAvailable;
        private readonly bool _ownsGammaController;
        private readonly ObservableCollection<ProgramProfileRow> _rows = new ObservableCollection<ProgramProfileRow>();
        private bool _healthy;
        private bool _synchronizingSelection;
        private bool _gammaOperationPending;
        private bool _closed;
        private string _requestedGammaPath;
        private string _gammaError;

        public ProgramProfilesWindow() : this(new ProgramProfileStore(), new ProgramProfileLauncher()) { }

        public ProgramProfilesWindow(ProgramProfileStore store, ProgramProfileLauncher launcher)
            : this(store, launcher, null) { }

        public ProgramProfilesWindow(ProgramProfileStore store, ProgramProfileLauncher launcher,
            IProgramGammaController gammaController)
            : this(store, launcher, gammaController, true) { }

        // A shared controller outlives this window: closing only stops its
        // session, and a session still winding down keeps the next window locked.
        public ProgramProfilesWindow(ProgramProfileStore store, ProgramProfileLauncher launcher,
            IProgramGammaController gammaController, bool ownsGammaController)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
            _gammaAvailable = gammaController != null;
            _ownsGammaController = ownsGammaController || gammaController == null;
            _gammaController = gammaController ?? new UnavailableProgramGammaController();
            InitializeComponent();
            ProgramList.ItemsSource = _rows;
            ModeSelector.ItemsSource = Enum.GetValues(typeof(ProgramColorMode)).Cast<ProgramColorMode>()
                .Select(mode => new ProgramColorChoice { Mode = mode }).ToArray();
            _gammaController.StateChanged += OnGammaStateChanged;
            // A shared controller may still own a session from a previous window;
            // the locked list must show that program, not the first entry.
            Reload(_gammaController.ActiveExecutablePath);
        }

        private ProgramProfileRow Selected => ProgramList.SelectedItem as ProgramProfileRow;
        private bool GammaBusy => _gammaOperationPending || _gammaController.IsBusy;
        private bool GammaSessionLocked => GammaBusy || _gammaController.IsActive;

        private void Reload(string preferredPath = null)
        {
            try
            {
                var profiles = _store.Load();
                ReplaceRows(profiles, preferredPath ?? Selected?.Path);
                _healthy = true;
                ShowStatus("Gespeicherte Profile starten keine Programme und aktivieren SDR-Gamma nicht automatisch.");
            }
            catch (Exception ex)
            {
                _healthy = false;
                ShowStatus("Laden fehlgeschlagen: " + ex.Message, true);
            }
            UpdateControls();
        }

        private void ReplaceRows(IEnumerable<ProgramLaunchProfile> profiles, string selectedPath)
        {
            var newRows = profiles.Select(profile => new ProgramProfileRow(profile)).ToList();
            _synchronizingSelection = true;
            try
            {
                _rows.Clear();
                foreach (var row in newRows) _rows.Add(row);
                ProgramList.SelectedItem = _rows.FirstOrDefault(row => string.Equals(row.Path, selectedPath, StringComparison.OrdinalIgnoreCase))
                    ?? _rows.FirstOrDefault();
            }
            finally { _synchronizingSelection = false; }
            SynchronizeSelection();
            CountLabel.Text = _rows.Count + " gespeicherte Programme";
            EmptyLabel.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Persist(IEnumerable<ProgramLaunchProfile> profiles, string selectedPath)
        {
            var snapshot = profiles.Select(profile => profile.Copy()).ToList();
            _store.Save(snapshot);
            ReplaceRows(snapshot, selectedPath);
            ShowStatus("Programmliste gespeichert. SDR-Gamma wird nur über Test oder Erzwingen aktiviert.");
            UpdateControls();
        }

        private void UpdateControls()
        {
            if (_closed || LaunchButton == null || GammaStatusLabel == null) return;
            var selected = Selected;
            var hasSelection = _healthy && selected != null;
            var sessionLocked = GammaSessionLocked;
            CountLabel.Text = _healthy ? _rows.Count + " gespeicherte Programme" : "Programmliste nicht verfügbar";
            EmptyLabel.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyLabel.Text = _healthy
                ? "Noch keine Programme. Wähle die EXE des gewünschten Programms aus."
                : "Die Programmliste konnte nicht geladen werden. Details stehen unten.";
            AddButton.IsEnabled = _healthy && !sessionLocked;
            ReloadButton.IsEnabled = !sessionLocked;
            RemoveButton.IsEnabled = hasSelection && !sessionLocked;
            ProgramList.IsEnabled = !sessionLocked;
            ModeSelector.IsEnabled = hasSelection && selected.IsSupported && !sessionLocked;
            ForceSdrGammaToggle.IsEnabled = hasSelection && !sessionLocked;
            // SelectionChanged may fire before SelectedValue has been updated by WPF.
            var choice = ModeSelector.SelectedItem as ProgramColorChoice;
            ProgramColorMode? mode = selected == null ? (ProgramColorMode?)null
                : selected.IsSupported && choice != null ? choice.Mode : ProgramColorMode.Default;
            var forceGamma = ForceSdrGammaToggle.IsChecked == true;
            var pending = selected != null && mode.HasValue
                && (mode.Value != selected.Profile.ColorMode || forceGamma != selected.Profile.ForceSdrGamma);
            SaveButton.IsEnabled = hasSelection && pending && !sessionLocked;
            LaunchButton.IsEnabled = hasSelection && selected.IsSupported && !pending && !sessionLocked;
            LaunchButton.ToolTip = hasSelection && !selected.IsSupported
                ? "Dieses Programm selbst starten. Der Startknopf unterstützt Brave, Chrome, Edge und Chromium."
                : "Startet das gespeicherte Browserprofil; aktiviert keine Gamma-Sitzung.";
            SelectedProgramLabel.Text = selected?.Name ?? "Programm auswählen";
            SelectedProgramLabel.ToolTip = selected?.Path;
            ModeDescription.Text = !_healthy ? "Die gespeicherte Datei wurde nicht verändert. Nach Beheben des Ladefehlers bitte Neu laden wählen."
                : selected != null && !selected.IsSupported ? "Dieses Programm selbst starten. Browser-Farbraumparameter sind hier nicht verfügbar; SDR-Gamma lässt sich separat für ein laufendes Fenster testen."
                : mode.HasValue ? ProgramProfileRules.Description(mode.Value)
                : "Ein Programm auswählen oder über EXE hinzufügen aufnehmen.";
            ArgumentsLabel.Text = _healthy && selected != null && !selected.IsSupported ? "Keine Browser-Startparameter"
                : _healthy && mode.HasValue
                ? (mode.Value == ProgramColorMode.Default ? "Kein zusätzlicher Startparameter" : ProgramProfileRules.Arguments(mode.Value))
                : string.Empty;
            if (_healthy && pending) ArgumentsLabel.Text += " · Noch nicht gespeichert";

            var unavailableReason = _gammaAvailable ? _gammaController.UnavailableReason : null;
            var canStartGamma = _gammaAvailable && unavailableReason == null && hasSelection && selected.IsExecutableAvailable
                && selected.Profile.ForceSdrGamma && forceGamma && !pending && !sessionLocked;
            GammaTestButton.IsEnabled = canStartGamma;
            GammaActivateButton.IsEnabled = canStartGamma;
            // Stop remains available while a test or activation is awaiting completion.
            GammaStopButton.IsEnabled = _gammaAvailable && sessionLocked;
            GammaHintLabel.Text = !_healthy ? "Gamma-Aktionen sind bei einem Ladefehler gesperrt."
                : unavailableReason != null && !sessionLocked ? unavailableReason
                : !hasSelection ? "Zuerst ein Programm aus der Liste auswählen."
                : sessionLocked ? "Für ein anderes Programm zuerst Erzwingen stoppen wählen."
                : !selected.IsExecutableAvailable ? "Die gespeicherte EXE fehlt. Bitte den vorhandenen Programmpfad hinzufügen."
                : pending ? "Die geänderte Auswahl zuerst mit Profil speichern übernehmen."
                : !forceGamma ? "Zum Testen die Gamma-Option aktivieren und das Profil speichern."
                : "Test und Erzwingen gelten für die bereits laufende Anwendung.";
            GammaStatusLabel.Text = _gammaError ?? _gammaController.Status;
            GammaStatusLabel.Foreground = _gammaError != null ? System.Windows.Media.Brushes.Salmon
                : (System.Windows.Media.Brush)FindResource("MutedBrush");
            var activePath = _gammaController.ActiveExecutablePath ?? _requestedGammaPath;
            GammaTargetLabel.Text = sessionLocked && !string.IsNullOrWhiteSpace(activePath)
                ? "Sitzung: " + activePath : string.Empty;
            GammaTargetLabel.Visibility = string.IsNullOrEmpty(GammaTargetLabel.Text) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ShowStatus(string message, bool error = false)
        {
            if (_closed) return;
            StatusLabel.Text = message;
            StatusLabel.Foreground = error ? System.Windows.Media.Brushes.Salmon
                : (System.Windows.Media.Brush)FindResource("MutedBrush");
        }

        private void OnAdd(object sender, RoutedEventArgs e)
        {
            if (!_healthy || GammaSessionLocked) return;
            var dialog = new OpenFileDialog { Title = "Programm für die Liste auswählen", Filter = "Programme (*.exe)|*.exe", CheckFileExists = true };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                var path = ProgramProfileRules.NormalizePath(dialog.FileName);
                if (!File.Exists(path)) throw new FileNotFoundException("Die ausgewählte EXE ist nicht vorhanden.", path);
                var info = ProgramExecutableInspector.Inspect(path);
                var existing = _rows.FirstOrDefault(row => string.Equals(row.Path, path, StringComparison.OrdinalIgnoreCase));
                if (existing != null) { ProgramList.SelectedItem = existing; ShowStatus("Dieses Programm steht bereits in der Liste."); return; }
                Persist(_rows.Select(row => row.Profile).Concat(new[] { new ProgramLaunchProfile
                {
                    ExecutablePath = path,
                    ColorMode = info.IsSupported ? ProgramColorMode.Srgb : ProgramColorMode.Default,
                    ForceSdrGamma = false
                } }), path);
            }
            catch (Exception ex) { ShowStatus("Hinzufügen fehlgeschlagen: " + ex.Message, true); }
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            var choice = ModeSelector.SelectedItem as ProgramColorChoice;
            if (!_healthy || Selected == null || choice == null || GammaSessionLocked) return;
            try
            {
                var selected = Selected;
                var updated = selected.Profile.Copy();
                updated.ColorMode = selected.IsSupported ? choice.Mode : ProgramColorMode.Default;
                updated.ForceSdrGamma = ForceSdrGammaToggle.IsChecked == true;
                Persist(_rows.Select(row => row == selected ? updated : row.Profile), selected.Path);
            }
            catch (Exception ex) { ShowStatus("Speichern fehlgeschlagen: " + ex.Message, true); }
        }

        private void OnRemove(object sender, RoutedEventArgs e)
        {
            if (!_healthy || Selected == null || GammaSessionLocked) return;
            try { Persist(_rows.Where(row => row != Selected).Select(row => row.Profile).ToList(), null); }
            catch (Exception ex) { ShowStatus("Entfernen fehlgeschlagen: " + ex.Message, true); }
        }

        private void OnLaunch(object sender, RoutedEventArgs e)
        {
            if (!LaunchButton.IsEnabled || Selected == null) return;
            var profile = Selected.Profile.Copy();
            if (profile.ColorMode != ProgramColorMode.Default && MessageBox.Show(this,
                ProgramProfileRules.Description(profile.ColorMode) + "\n\n" + Selected.Name + " jetzt mit diesem Profil starten?",
                "Farbraum nur für dieses Programm erzwingen", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;
            try
            {
                _launcher.Launch(profile);
                ShowStatus("Browserstart angefordert. Die Gamma-Sitzung wird separat über Test oder Erzwingen aktiviert.");
            }
            catch (Exception ex) { ShowStatus("Start nicht ausgeführt: " + ex.Message, true); }
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_synchronizingSelection) return;
            SynchronizeSelection();
        }

        private void SynchronizeSelection()
        {
            if (ModeSelector == null || ForceSdrGammaToggle == null) return;
            _synchronizingSelection = true;
            try
            {
                ModeSelector.SelectedValue = Selected == null ? null
                    : (object)(Selected.IsSupported ? Selected.Profile.ColorMode : ProgramColorMode.Default);
                ForceSdrGammaToggle.IsChecked = Selected?.Profile.ForceSdrGamma == true;
            }
            finally { _synchronizingSelection = false; }
            _gammaError = null;
            UpdateControls();
        }

        private void OnModeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_synchronizingSelection) UpdateControls();
        }

        private void OnGammaToggleChanged(object sender, RoutedEventArgs e)
        {
            if (!_synchronizingSelection) UpdateControls();
        }

        private async void OnGammaTest(object sender, RoutedEventArgs e) => await RunGammaAsync(true);
        private async void OnGammaActivate(object sender, RoutedEventArgs e) => await RunGammaAsync(false);

        private async Task RunGammaAsync(bool test)
        {
            var button = test ? GammaTestButton : GammaActivateButton;
            if (!button.IsEnabled || Selected == null || !_gammaAvailable
                || _gammaController.UnavailableReason != null || GammaSessionLocked) return;
            _requestedGammaPath = Selected.Path;
            _gammaError = null;
            _gammaOperationPending = true;
            UpdateControls();
            try
            {
                if (test) await _gammaController.TestAsync(_requestedGammaPath);
                else await _gammaController.ActivateAsync(_requestedGammaPath);
            }
            catch (OperationCanceledException)
            {
                ShowStatus("Gamma-Sitzung beendet.");
            }
            catch (Exception ex)
            {
                _gammaError = (test ? "Gamma-Test fehlgeschlagen: " : "Gamma-Erzwingen fehlgeschlagen: ") + ex.Message;
                ShowStatus(_gammaError, true);
            }
            finally
            {
                _gammaOperationPending = false;
                _requestedGammaPath = null;
                UpdateControls();
            }
        }

        private void OnGammaStop(object sender, RoutedEventArgs e)
        {
            if (!GammaStopButton.IsEnabled) return;
            try
            {
                _gammaController.Stop();
                _gammaError = null;
                ShowStatus("Stoppen der Gamma-Sitzung angefordert. Die Anwendung läuft weiter.");
            }
            catch (Exception ex)
            {
                _gammaError = "Stoppen fehlgeschlagen: " + ex.Message;
                ShowStatus(_gammaError, true);
            }
            UpdateControls();
        }

        private void OnGammaStateChanged(object sender, EventArgs e)
        {
            if (_closed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            if (Dispatcher.CheckAccess()) UpdateControls();
            else Dispatcher.BeginInvoke(new Action(UpdateControls));
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);
            if (e.Cancel) return;
            try { _gammaController.Stop(); }
            catch (Exception ex)
            {
                e.Cancel = true;
                _gammaError = "Gamma-Sitzung konnte nicht beendet werden: " + ex.Message;
                ShowStatus(_gammaError, true);
                UpdateControls();
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _closed = true;
            _gammaController.StateChanged -= OnGammaStateChanged;
            try { if (_ownsGammaController) _gammaController.Dispose(); }
            catch (Exception ex) { Trace.TraceError("Gamma-Controller schließen: " + ex); }
            finally { base.OnClosed(e); }
        }

        private void OnReload(object sender, RoutedEventArgs e)
        {
            if (!GammaSessionLocked) Reload();
        }
        private void OnClose(object sender, RoutedEventArgs e) => Close();
    }
}
