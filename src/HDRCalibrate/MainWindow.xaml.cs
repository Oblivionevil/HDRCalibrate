using HDRCalibrate.ViewModels;
using System.Windows;

namespace HDRCalibrate
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;

        public MainWindow()
            : this(new MainViewModel())
        {
        }

        public MainWindow(MainViewModel viewModel)
        {
            _viewModel = viewModel ?? throw new System.ArgumentNullException(nameof(viewModel));
            InitializeComponent();
            DataContext = _viewModel;
            Loaded += OnLoaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoaded;
            await _viewModel.InitializeAsync();
        }

        private void OnProgramProfiles(object sender, RoutedEventArgs e)
        {
            if (_viewModel.IsBusy) return;
            new ProgramProfilesWindow { Owner = this }.ShowDialog();
        }
    }
}
