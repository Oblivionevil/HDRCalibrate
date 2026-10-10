using HDRCalibrate.ViewModels;
using HDRCalibrate.Services;
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

namespace HDRCalibrate
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private readonly DispatcherTimer _cooldownTimer;
        // One controller for the main window lifetime: a session that is still
        // winding down after the program list closed keeps the list locked.
        private ProgramGammaController _gammaController;

        public MainWindow()
            : this(new MainViewModel())
        {
        }

        public MainWindow(MainViewModel viewModel)
        {
            _viewModel = viewModel ?? throw new System.ArgumentNullException(nameof(viewModel));
            InitializeComponent();
            DataContext = _viewModel;
            _cooldownTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _cooldownTimer.Tick += OnCooldownTick;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            IsVisibleChanged += OnVisibilityChanged;
            Closed += OnClosed;
            Loaded += OnLoaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoaded;
            await _viewModel.InitializeAsync();
            _viewModel.RefreshRestartCooldown();
            UpdateCooldownTimer();
        }

        private void OnCooldownTick(object sender, EventArgs e)
        {
            _viewModel.RefreshRestartCooldown();
            UpdateCooldownTimer();
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.IsRestartCooldownActive)) UpdateCooldownTimer();
        }

        private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (IsVisible) _viewModel.RefreshRestartCooldown();
            UpdateCooldownTimer();
        }

        private void UpdateCooldownTimer()
        {
            if (IsVisible && _viewModel.IsRestartCooldownActive) _cooldownTimer.Start();
            else _cooldownTimer.Stop();
        }

        private void OnClosed(object sender, EventArgs e)
        {
            _cooldownTimer.Stop();
            _cooldownTimer.Tick -= OnCooldownTick;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            IsVisibleChanged -= OnVisibilityChanged;
            Closed -= OnClosed;
            _gammaController?.Dispose();
        }

        private void OnProgramProfiles(object sender, RoutedEventArgs e)
        {
            if (_viewModel.IsBusy) return;
            if (_gammaController == null)
                _gammaController = new ProgramGammaController(_viewModel.GetProgramGammaApproval);
            new ProgramProfilesWindow(new ProgramProfileStore(), new ProgramProfileLauncher(),
                _gammaController, false) { Owner = this }.ShowDialog();
        }
    }
}
