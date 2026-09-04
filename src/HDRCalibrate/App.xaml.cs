using HDRCalibrate.Services;
using HDRCalibrate.ViewModels;
using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace HDRCalibrate
{
    public partial class App : Application
    {
        private static readonly TimeSpan ExitExistingTimeout = TimeSpan.FromSeconds(30);

        private SingleInstanceCoordinator _singleInstance;
        private MainWindow _mainWindow;
        private MainViewModel _viewModel;
        private Forms.NotifyIcon _trayIcon;
        private Icon _trayIconImage;
        private Forms.ContextMenuStrip _trayMenu;
        private Forms.ToolStripMenuItem _autostartMenuItem;
        private Forms.ToolStripMenuItem _startupActivationMenuItem;
        private readonly ExitRequestGate _exitGate = new ExitRequestGate();
        private bool _exitRequested;
        private bool _trayHintShown;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var startInTray = StartupOptions.ShouldStartInTray(e.Args);
            var activateAtStartup = StartupOptions.ShouldActivateAtStartup(e.Args);
            var exitExisting = StartupOptions.ShouldExitExisting(e.Args);
            _singleInstance = new SingleInstanceCoordinator();
            var isPrimary = _singleInstance.TryAcquire();

            if (exitExisting)
            {
                if (isPrimary)
                {
                    Shutdown(0);
                    return;
                }

                var exited = false;
                try
                {
                    _singleInstance.SignalExit();
                    exited = _singleInstance.WaitForPrimaryExit(ExitExistingTimeout);
                }
                catch
                {
                    exited = false;
                }
                Shutdown(exited ? 0 : 5);
                return;
            }

            if (!isPrimary)
            {
                if (!startInTray) _singleInstance.SignalActivation();
                Shutdown(0);
                return;
            }

            _viewModel = new MainViewModel();
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _mainWindow = new MainWindow(_viewModel);
            _mainWindow.Closing += OnMainWindowClosing;
            MainWindow = _mainWindow;

            InitializeTrayIcon();
            _singleInstance.StartListening(
                () => Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(ShowMainWindow)),
                () => Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(RequestExit)));
            if (!startInTray) ShowMainWindow();
            else if (activateAtStartup) BeginStartupActivation();
        }

        protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
        {
            _exitRequested = true;
            base.OnSessionEnding(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _exitRequested = true;
            if (_viewModel != null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }
            _trayIconImage?.Dispose();
            _trayIconImage = null;
            _trayMenu?.Dispose();
            _trayMenu = null;
            _singleInstance?.Dispose();
            _singleInstance = null;
            base.OnExit(e);
        }

        private void InitializeTrayIcon()
        {
            _trayMenu = new Forms.ContextMenuStrip();
            _trayMenu.Items.Add(new Forms.ToolStripMenuItem("Öffnen", null, (sender, args) => ShowMainWindow()));
            _trayMenu.Items.Add(new Forms.ToolStripSeparator());
            _autostartMenuItem = new Forms.ToolStripMenuItem("Mit Windows starten (im Tray)", null, OnToggleAutostart)
            {
                CheckOnClick = false
            };
            _trayMenu.Items.Add(_autostartMenuItem);
            _startupActivationMenuItem = new Forms.ToolStripMenuItem(
                "SDR-Korrektur beim Windows-Start aktivieren (UAC)",
                null,
                OnToggleStartupActivation)
            {
                CheckOnClick = false
            };
            _trayMenu.Items.Add(_startupActivationMenuItem);
            _trayMenu.Items.Add(new Forms.ToolStripSeparator());
            _trayMenu.Items.Add(new Forms.ToolStripMenuItem("Beenden", null, (sender, args) => RequestExit()));
            _trayMenu.Opening += (sender, args) =>
            {
                _viewModel.RefreshAutostartState();
                UpdateAutostartTrayState();
            };

            _trayIconImage = LoadTrayIcon();
            _trayIcon = new Forms.NotifyIcon
            {
                Icon = _trayIconImage,
                Text = "HDR Calibrate",
                ContextMenuStrip = _trayMenu,
                Visible = true
            };
            _trayIcon.DoubleClick += (sender, args) => ShowMainWindow();
            UpdateAutostartTrayState();
        }

        private static Icon LoadTrayIcon()
        {
            var resource = GetResourceStream(new Uri("/HDRCalibrate;component/Assets/HDRCalibrate.ico", UriKind.Relative));
            if (resource == null) throw new InvalidOperationException("Das eingebettete HDR-Calibrate-Symbol fehlt.");
            using (resource.Stream)
            using (var icon = new Icon(
                resource.Stream,
                Forms.SystemInformation.SmallIconSize.Width,
                Forms.SystemInformation.SmallIconSize.Height))
                return (Icon)icon.Clone();
        }

        private void OnToggleAutostart(object sender, EventArgs e)
        {
            _viewModel.ToggleAutostart();
            UpdateAutostartTrayState();
        }

        private void OnToggleStartupActivation(object sender, EventArgs e)
        {
            if (!_viewModel.IsStartupActivationEnabled) ShowMainWindow();
            _viewModel.ToggleStartupActivation();
            UpdateAutostartTrayState();
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.IsAutostartEnabled))
                UpdateAutostartTrayState();
            if (e.PropertyName == nameof(MainViewModel.IsStartupActivationEnabled)
                || e.PropertyName == nameof(MainViewModel.CanToggleStartupActivation))
                UpdateAutostartTrayState();
            if (e.PropertyName == nameof(MainViewModel.IsBusy) && _exitGate.CanComplete(_viewModel.IsBusy))
                Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(ExitApplication));
        }

        private void UpdateAutostartTrayState()
        {
            if (_autostartMenuItem != null && _viewModel != null)
                _autostartMenuItem.Checked = _viewModel.IsAutostartEnabled;
            if (_startupActivationMenuItem != null && _viewModel != null)
            {
                _startupActivationMenuItem.Checked = _viewModel.IsStartupActivationEnabled;
                _startupActivationMenuItem.Enabled = _viewModel.CanToggleStartupActivation;
            }
        }

        private async void BeginStartupActivation()
        {
            StartupActivationResult result;
            try
            {
                result = await _viewModel.TryActivateAtWindowsStartupAsync(true);
            }
            catch (Exception ex)
            {
                result = new StartupActivationResult(
                    StartupActivationDecision.ApprovalInvalid,
                    false,
                    false,
                    "Startaktivierung wurde nicht ausgeführt: " + ex.Message);
            }

            UpdateAutostartTrayState();
            if (string.IsNullOrWhiteSpace(result.Message) || _trayIcon == null) return;
            _trayIcon.ShowBalloonTip(
                8000,
                result.Success ? "HDR Calibrate" : "Startaktivierung nicht ausgeführt",
                result.Message,
                result.Success ? Forms.ToolTipIcon.Info : Forms.ToolTipIcon.Warning);
        }

        private void ShowMainWindow()
        {
            if (_mainWindow == null || _exitRequested) return;
            if (!_mainWindow.IsVisible) _mainWindow.Show();
            if (_mainWindow.WindowState == WindowState.Minimized)
                _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
        }

        private void OnMainWindowClosing(object sender, CancelEventArgs e)
        {
            if (_exitRequested) return;
            e.Cancel = true;
            _mainWindow.Hide();
            if (_trayHintShown || _trayIcon == null) return;

            _trayHintShown = true;
            _trayIcon.ShowBalloonTip(
                5000,
                "HDR Calibrate läuft weiter",
                "Das Fenster wurde ins Infobereich-Symbol verschoben. Über das Symbol können Sie die App öffnen oder vollständig beenden.",
                Forms.ToolTipIcon.Info);
        }

        private void RequestExit()
        {
            if (_exitRequested) return;
            if (!_exitGate.Request(_viewModel?.IsBusy == true))
            {
                ShowMainWindow();
                _viewModel.ReportExitDeferred();
                _trayIcon?.ShowBalloonTip(
                    5000,
                    "Beenden wird aufgeschoben",
                    "Ein sicherheitskritischer DWM-Vorgang läuft. HDR Calibrate beendet sich automatisch, sobald er abgeschlossen ist.",
                    Forms.ToolTipIcon.Warning);
                return;
            }
            ExitApplication();
        }

        private void ExitApplication()
        {
            if (_exitRequested) return;
            if (!_exitGate.Request(_viewModel?.IsBusy == true))
            {
                RequestExit();
                return;
            }
            _exitRequested = true;
            _exitGate.Complete();
            _mainWindow?.Close();
            Shutdown(0);
        }
    }
}
