using HDRCalibrate.Setup.Core;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;

namespace HDRCalibrate.Setup
{
    public partial class SetupWindow : Window
    {
        private enum Stage { Ready, Running, Done }

        private readonly SetupMode _mode;
        private readonly SetupContext _context;
        private readonly ExistingInstallation _existing;
        private readonly PayloadPackage _package;
        private readonly string _setupExecutable;
        private readonly string _logPath;
        private readonly SetupText _text;
        private Stage _stage = Stage.Ready;
        private string _installedApp;

        public SetupWindow(SetupMode mode, SetupContext context, ExistingInstallation existing,
            PayloadPackage package, string setupExecutable, string logPath)
        {
            _mode = mode;
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _existing = existing ?? throw new ArgumentNullException(nameof(existing));
            _package = package;
            _setupExecutable = setupExecutable;
            _logPath = logPath;
            _text = context.Text;
            InitializeComponent();
            var version = typeof(SetupWindow).Assembly.GetName().Version.ToString(3);
            Title = _text.WindowTitle(version);
            ExitCode = 2;
            if (mode == SetupMode.Install) ShowInstallOptions(version);
            else ShowUninstallConfirmation();
        }

        /// <summary>0 success, 1 failure, 2 cancelled.</summary>
        public int ExitCode { get; private set; }

        private void ShowInstallOptions(string version)
        {
            HeadingText.Text = _existing.IsInstalled ? _text.UpdateHeading : _text.InstallHeading;
            SubheadingText.Text = "HDR Calibrate " + version + " · x64 · .NET Framework 4.8.1";
            IntroText.Text = _text.InstallIntro;
            TargetText.Text = _text.TargetDirectory(_existing.Directory);
            VersionText.Text = _existing.Version != null ? _text.InstalledVersion(_existing.Version) : string.Empty;
            VersionText.Visibility = _existing.Version != null ? Visibility.Visible : Visibility.Collapsed;
            NoteText.Text = _existing.AppExists ? _text.RunningAppNote : string.Empty;
            NoteText.Visibility = _existing.AppExists ? Visibility.Visible : Visibility.Collapsed;
            DesktopCheck.Content = _text.DesktopTask;
            AutostartCheck.Content = _text.AutoStartTask;
            // Upgrades reflect the current state instead of reviving an old choice.
            DesktopCheck.IsChecked = _existing.DesktopShortcutExists;
            AutostartCheck.IsChecked = _existing.AutostartRegistered;
            PrimaryButton.Content = _existing.IsInstalled ? _text.UpdateButton : _text.InstallButton;
            SecondaryButton.Content = _text.CancelButton;
            if (_package == null) ShowResult(false, _text.PayloadMissing, null);
        }

        private void ShowUninstallConfirmation()
        {
            HeadingText.Text = _text.UninstallHeading;
            SubheadingText.Text = ProductInfo.AppName + (_existing.Version != null ? " " + _existing.Version : string.Empty);
            if (!_existing.IsInstalled)
            {
                ShowResult(false, _text.NotInstalled, null);
                return;
            }
            IntroText.Text = _text.UninstallIntro(_existing.Directory);
            TargetText.Text = _text.UninstallKeepsSettings;
            VersionText.Visibility = Visibility.Collapsed;
            NoteText.Text = _existing.AppExists ? _text.RunningAppNote : string.Empty;
            NoteText.Visibility = _existing.AppExists ? Visibility.Visible : Visibility.Collapsed;
            DesktopCheck.Visibility = Visibility.Collapsed;
            AutostartCheck.Visibility = Visibility.Collapsed;
            PrimaryButton.Content = _text.UninstallButton;
            SecondaryButton.Content = _text.CancelButton;
        }

        private async void OnPrimary(object sender, RoutedEventArgs e)
        {
            if (_stage == Stage.Done)
            {
                if (ExitCode == 0 && LaunchCheck.Visibility == Visibility.Visible && LaunchCheck.IsChecked == true)
                    LaunchInstalledApp();
                Close();
                return;
            }
            if (_stage != Stage.Ready) return;
            _stage = Stage.Running;
            OptionsPanel.Visibility = Visibility.Collapsed;
            ProgressPanel.Visibility = Visibility.Visible;
            PrimaryButton.IsEnabled = false;
            SecondaryButton.IsEnabled = false;
            var progress = new Progress<string>(message => ProgressText.Text = message);
            try
            {
                if (_mode == SetupMode.Install)
                {
                    var options = new InstallOptions
                    {
                        DesktopShortcut = DesktopCheck.IsChecked == true,
                        Autostart = AutostartCheck.IsChecked == true
                    };
                    var result = await Task.Run(() =>
                        new InstallEngine(_context).Install(_package, _setupExecutable, options, progress));
                    _installedApp = result.AppExecutable;
                    ShowResult(true, _text.InstallDone(_package.Manifest.Version), null);
                    LaunchCheck.Content = _text.LaunchApp;
                    LaunchCheck.Visibility = Visibility.Visible;
                }
                else
                {
                    var result = await Task.Run(() => new UninstallEngine(_context).Uninstall(_package?.Manifest, progress));
                    var details = string.Empty;
                    if (result.RetainedNativeDirectories.Count > 0)
                        details = _text.NativeHookRetained(string.Join("\n", result.RetainedNativeDirectories));
                    if (result.RemainingDirectory != null)
                        details += (details.Length > 0 ? "\n\n" : string.Empty) + _text.RemainingFiles(result.RemainingDirectory);
                    ShowResult(true, _text.UninstallDone, details);
                }
            }
            catch (SetupException ex)
            {
                _context.Log.Write("Abbruch: " + ex);
                ShowResult(false, ex.Message, null);
            }
            catch (Exception ex)
            {
                _context.Log.Write("Unerwarteter Fehler: " + ex);
                ShowResult(false, _mode == SetupMode.Install ? _text.InstallFailed(ex.Message) : _text.UninstallFailed(ex.Message), null);
            }
        }

        private void OnSecondary(object sender, RoutedEventArgs e)
        {
            if (_stage == Stage.Running) return;
            Close();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // Never interrupt a running file transaction.
            if (_stage == Stage.Running) e.Cancel = true;
            base.OnClosing(e);
        }

        private void ShowResult(bool success, string message, string details)
        {
            _stage = Stage.Done;
            ExitCode = success ? 0 : 1;
            OptionsPanel.Visibility = Visibility.Collapsed;
            ProgressPanel.Visibility = Visibility.Collapsed;
            ResultPanel.Visibility = Visibility.Visible;
            if (!success) HeadingText.Text = _text.ErrorHeading;
            ResultText.Text = message;
            ResultText.Foreground = success ? (System.Windows.Media.Brush)FindResource("AccentBrush") : System.Windows.Media.Brushes.Salmon;
            DetailText.Text = details ?? string.Empty;
            DetailText.Visibility = string.IsNullOrEmpty(details) ? Visibility.Collapsed : Visibility.Visible;
            LogText.Text = string.IsNullOrEmpty(_logPath) ? string.Empty : _text.LogLocation(_logPath);
            PrimaryButton.Content = success ? _text.FinishButton : _text.CloseButton;
            PrimaryButton.IsEnabled = true;
            SecondaryButton.Visibility = Visibility.Collapsed;
        }

        private void LaunchInstalledApp()
        {
            try
            {
                using (Process.Start(new ProcessStartInfo
                {
                    FileName = _installedApp,
                    WorkingDirectory = System.IO.Path.GetDirectoryName(_installedApp),
                    UseShellExecute = true
                })) { }
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is Win32Exception)
            {
                _context.Log.Write("Start der Anwendung fehlgeschlagen: " + ex.Message);
            }
        }
    }
}
