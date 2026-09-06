using HDRCalibrate.Services;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace HDRCalibrate
{
    public sealed class ProgramProfileRow
    {
        public ProgramLaunchProfile Profile { get; }
        public string Name { get; }
        public string Path => Profile.ExecutablePath;
        public string ModeLabel => ProgramProfileRules.Label(Profile.ColorMode);
        public string Status { get; }
        public bool IsSupported { get; }

        public ProgramProfileRow(ProgramLaunchProfile profile)
        {
            Profile = profile.Copy();
            var info = ProgramExecutableInspector.Inspect(profile.ExecutablePath);
            Name = info.Name;
            Status = info.Status;
            IsSupported = info.IsSupported;
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
        private readonly ObservableCollection<ProgramProfileRow> _rows = new ObservableCollection<ProgramProfileRow>();
        private bool _healthy;

        public ProgramProfilesWindow() : this(new ProgramProfileStore(), new ProgramProfileLauncher()) { }

        public ProgramProfilesWindow(ProgramProfileStore store, ProgramProfileLauncher launcher)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
            InitializeComponent();
            ProgramList.ItemsSource = _rows;
            ModeSelector.ItemsSource = Enum.GetValues(typeof(ProgramColorMode)).Cast<ProgramColorMode>()
                .Select(mode => new ProgramColorChoice { Mode = mode }).ToArray();
            Reload();
        }

        private ProgramProfileRow Selected => ProgramList.SelectedItem as ProgramProfileRow;

        private void Reload()
        {
            try
            {
                var profiles = _store.Load();
                ReplaceRows(profiles, Selected?.Path);
                _healthy = true;
                ShowStatus("Profile werden gespeichert, aber niemals automatisch gestartet.");
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
            _rows.Clear();
            foreach (var row in newRows) _rows.Add(row);
            ProgramList.SelectedItem = _rows.FirstOrDefault(row => string.Equals(row.Path, selectedPath, StringComparison.OrdinalIgnoreCase))
                ?? _rows.FirstOrDefault();
            CountLabel.Text = _rows.Count + " gespeicherte Programme";
            EmptyLabel.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Persist(IEnumerable<ProgramLaunchProfile> profiles, string selectedPath)
        {
            var snapshot = profiles.Select(profile => profile.Copy()).ToList();
            _store.Save(snapshot);
            ReplaceRows(snapshot, selectedPath);
            ShowStatus("Programmliste gespeichert. Kein Programm wurde gestartet oder verändert.");
            UpdateControls();
        }

        private void UpdateControls()
        {
            if (LaunchButton == null) return;
            var selected = Selected;
            var hasSelection = _healthy && selected != null;
            CountLabel.Text = _healthy ? _rows.Count + " gespeicherte Programme" : "Programmliste nicht verfügbar";
            EmptyLabel.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyLabel.Text = _healthy
                ? "Noch keine Programme. Wähle die EXE des gewünschten Browsers aus."
                : "Die Programmliste konnte nicht geladen werden. Details stehen unten.";
            AddButton.IsEnabled = _healthy;
            RemoveButton.IsEnabled = hasSelection;
            ModeSelector.IsEnabled = hasSelection;
            // SelectionChanged may fire before SelectedValue has been updated by WPF.
            var choice = ModeSelector.SelectedItem as ProgramColorChoice;
            ProgramColorMode? mode = choice == null ? (ProgramColorMode?)null : choice.Mode;
            var pending = selected != null && mode.HasValue && mode.Value != selected.Profile.ColorMode;
            SaveButton.IsEnabled = hasSelection && pending;
            LaunchButton.IsEnabled = hasSelection && selected.IsSupported && !pending;
            ModeDescription.Text = !_healthy ? "Die gespeicherte Datei wurde nicht verändert. Nach Beheben des Ladefehlers bitte Neu laden wählen."
                : mode.HasValue ? ProgramProfileRules.Description(mode.Value)
                : "Ein Programm auswählen oder über EXE hinzufügen aufnehmen.";
            ArgumentsLabel.Text = _healthy && mode.HasValue
                ? (mode.Value == ProgramColorMode.Default ? "Kein zusätzlicher Startparameter" : ProgramProfileRules.Arguments(mode.Value))
                : string.Empty;
            if (_healthy && pending) ArgumentsLabel.Text += " · Noch nicht gespeichert";
        }

        private void ShowStatus(string message, bool error = false)
        {
            StatusLabel.Text = message;
            StatusLabel.Foreground = error ? System.Windows.Media.Brushes.Salmon
                : (System.Windows.Media.Brush)FindResource("MutedBrush");
        }

        private void OnAdd(object sender, RoutedEventArgs e)
        {
            if (!_healthy) return;
            var dialog = new OpenFileDialog { Title = "Brave, Chrome, Edge oder Chromium auswählen", Filter = "Programme (*.exe)|*.exe", CheckFileExists = true };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                var path = ProgramProfileRules.NormalizePath(dialog.FileName);
                if (!ProgramExecutableInspector.Inspect(path).IsSupported)
                    throw new InvalidOperationException("Unterstützt werden derzeit Brave, Chrome, Edge und Chromium. Für andere EXEs ist kein verlässlicher Farbraum-Startparameter hinterlegt.");
                var existing = _rows.FirstOrDefault(row => string.Equals(row.Path, path, StringComparison.OrdinalIgnoreCase));
                if (existing != null) { ProgramList.SelectedItem = existing; ShowStatus("Dieses Programm steht bereits in der Liste."); return; }
                Persist(_rows.Select(row => row.Profile).Concat(new[] { new ProgramLaunchProfile
                {
                    ExecutablePath = path,
                    ColorMode = ProgramColorMode.Srgb
                } }), path);
            }
            catch (Exception ex) { ShowStatus("Hinzufügen fehlgeschlagen: " + ex.Message, true); }
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            var choice = ModeSelector.SelectedItem as ProgramColorChoice;
            if (!_healthy || Selected == null || choice == null) return;
            try
            {
                var selected = Selected;
                var updated = selected.Profile.Copy();
                updated.ColorMode = choice.Mode;
                Persist(_rows.Select(row => row == selected ? updated : row.Profile), selected.Path);
            }
            catch (Exception ex) { ShowStatus("Speichern fehlgeschlagen: " + ex.Message, true); }
        }

        private void OnRemove(object sender, RoutedEventArgs e)
        {
            if (!_healthy || Selected == null) return;
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
                ShowStatus("Start angefordert. Die tatsächliche Farbdarstellung bitte im Programm prüfen; kein DWM-Patch wurde ausgelöst.");
            }
            catch (Exception ex) { ShowStatus("Start nicht ausgeführt: " + ex.Message, true); }
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ModeSelector == null) return;
            ModeSelector.SelectedValue = Selected == null ? null : (object)Selected.Profile.ColorMode;
            UpdateControls();
        }

        private void OnModeChanged(object sender, SelectionChangedEventArgs e) => UpdateControls();
        private void OnReload(object sender, RoutedEventArgs e) => Reload();
        private void OnClose(object sender, RoutedEventArgs e) => Close();
    }
}
