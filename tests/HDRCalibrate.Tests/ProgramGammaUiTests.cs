using HDRCalibrate.Services;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HDRCalibrate.Tests
{
    internal static class ProgramGammaUiTests
    {
        private sealed class FakeGamma : IProgramGammaController
        {
            public event EventHandler StateChanged;
            public bool IsBusy { get; private set; }
            public bool IsActive { get; private set; }
            public string Status { get; private set; } = "Isolierter UI-Test · kein DWM-Zugriff";
            public string ActiveExecutablePath { get; private set; }
            public string Reason;
            public string UnavailableReason => Reason;
            public int Tests;
            public int Activations;
            public int Stops;
            public int Disposals;
            public Task TestAsync(string path) { Tests++; return Task.CompletedTask; }
            public Task ActivateAsync(string path)
            {
                Activations++; ActiveExecutablePath = path; IsActive = true;
                Status = "Simulierte aktive Sitzung · VRR ungeprüft";
                StateChanged?.Invoke(this, EventArgs.Empty);
                return Task.CompletedTask;
            }
            public void Stop() { Stops++; IsBusy = false; IsActive = false; Status = "Simulierte Sitzung beendet · kein DWM-Zugriff"; StateChanged?.Invoke(this, EventArgs.Empty); }
            public void Dispose() { Disposals++; }
        }

        public static void ListActionsAndOffscreenLayout()
        {
            Exception error = null;
            var thread = new Thread(() =>
            {
                try { CheckWindow(); }
                catch (Exception ex) { error = ex; }
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!thread.Join(20000)) throw new InvalidOperationException("Isolierter WPF-Komponententest hat sein Zeitbudget überschritten.");
            if (error != null) throw new InvalidOperationException("WPF-Programmliste: " + error.Message, error);
        }

        private static void CheckWindow()
        {
            // Renderings stay in program-gamma-ui for inspection; the per-run
            // profile store fixture is removed again.
            var output = Path.Combine(Path.GetDirectoryName(typeof(ProgramGammaUiTests).Assembly.Location), "program-gamma-ui");
            var fixture = Path.Combine(output, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixture);
            try { CheckWindow(output, fixture); }
            finally { if (Directory.Exists(fixture)) Directory.Delete(fixture, true); }
        }

        private static void CheckWindow(string output, string fixture)
        {
            var path = typeof(ProgramGammaUiTests).Assembly.Location;
            var store = new ProgramProfileStore(fixture);
            store.Save(new[] { new ProgramLaunchProfile { ExecutablePath = path, ColorMode = ProgramColorMode.Default } });
            Action<System.Diagnostics.ProcessStartInfo> rejectLaunch = info => { throw new InvalidOperationException("Kein echter Programmstart im Komponententest erlaubt."); };
            var launcher = new ProgramProfileLauncher(null, null, rejectLaunch, null, rejectLaunch);
            var gamma = new FakeGamma();
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
                new Uri("/HDRCalibrate;component/Theme.xaml", UriKind.Relative)));
            var window = new ProgramProfilesWindow(store, launcher, gamma);
            try
            {
                // Render only the WPF content tree. Do not Show the window, invoke
                // App.OnStartup, touch live profiles, or control another UI/process.
                var list = (ListBox)window.FindName("ProgramList");
                var toggle = (CheckBox)window.FindName("ForceSdrGammaToggle");
                var save = (Button)window.FindName("SaveButton");
                var test = (Button)window.FindName("GammaTestButton");
                var activate = (Button)window.FindName("GammaActivateButton");
                var stop = (Button)window.FindName("GammaStopButton");
                var launch = (Button)window.FindName("LaunchButton");
                list.SelectedIndex = 0;
                Require(!test.IsEnabled && !activate.IsEnabled && !stop.IsEnabled && !launch.IsEnabled,
                    "Ungültige Aktion vor ausdrücklichem Gamma-Opt-in oder Fremdprogramm als Browser startbar.");
                toggle.IsChecked = true;
                Require(save.IsEnabled && !test.IsEnabled, "Ungespeicherte Gamma-Auswahl wurde aktivierbar.");
                Click(save);
                Require(store.Load()[0].ForceSdrGamma && test.IsEnabled && activate.IsEnabled,
                    "Beliebige lokale EXE wurde nach gespeichertem Opt-in nicht verfügbar.");
                Require(gamma.Tests == 0 && gamma.Activations == 0, "Speichern aktivierte Gamma automatisch.");
                Click(test);
                Require(gamma.Tests == 1, "Testknopf ist nicht mit dem Gamma-Controller verbunden.");
                Click(activate);
                Require(gamma.Activations == 1 && stop.IsEnabled && !list.IsEnabled && !save.IsEnabled && !toggle.IsEnabled,
                    "Aktive Sitzung sperrt die Zielauswahl nicht oder Stop ist unerreichbar.");
                Render(window, output, "active", 1012, 770);
                Click(stop);
                Require(gamma.Stops == 1 && list.IsEnabled && test.IsEnabled && !stop.IsEnabled,
                    "Stop hebt die Sitzungssperre nicht auf.");
                Render(window, output, "ready", 1012, 770);
                Render(window, output, "minimum", 852, 630);

                // A known unsupported platform blocks Test/Activate before any
                // click, and a shared controller is not disposed by the window.
                var blockedGamma = new FakeGamma { Reason = "Simuliert: DWM-Build nicht freigegeben" };
                var blocked = new ProgramProfilesWindow(store, launcher, blockedGamma, false);
                try
                {
                    Require(((ListBox)blocked.FindName("ProgramList")).SelectedIndex == 0
                        && store.Load()[0].ForceSdrGamma
                        && !((Button)blocked.FindName("GammaTestButton")).IsEnabled
                        && !((Button)blocked.FindName("GammaActivateButton")).IsEnabled
                        && ((TextBlock)blocked.FindName("GammaHintLabel")).Text == blockedGamma.Reason,
                        "Ein bekannter nicht freigegebener Build ließ Gamma-Aktionen zu oder verschwieg den Grund.");
                }
                finally { blocked.Close(); }
                Require(blockedGamma.Stops == 1 && blockedGamma.Disposals == 0,
                    "Ein geteilter Controller wurde vom Fenster nicht gestoppt oder fälschlich entsorgt.");
            }
            finally
            {
                window.Close();
                application.Shutdown();
            }
            Require(gamma.Stops >= 2 && gamma.Disposals == 1, "Fensterschließen beendet/bereinigt den Controller nicht.");
        }

        private static void Render(ProgramProfilesWindow window, string output, string label, int width, int height)
        {
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(width, height));
            content.Arrange(new Rect(0, 0, width, height));
            content.UpdateLayout();
            foreach (var name in new[] { "ProgramList", "SaveButton", "LaunchButton", "StatusLabel" })
            {
                var control = (FrameworkElement)window.FindName(name);
                var bounds = control.TransformToAncestor(content).TransformBounds(new Rect(0, 0, control.ActualWidth, control.ActualHeight));
                Require(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= width + 1 && bounds.Bottom <= height + 1,
                    "Abgeschnittenes Bedienelement bei " + label + ": " + name);
            }
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            var background = new DrawingVisual();
            using (var drawing = background.RenderOpen())
                drawing.DrawRectangle((Brush)window.FindResource("WindowBrush"), null, new Rect(0, 0, width, height));
            bitmap.Render(background);
            bitmap.Render(content);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(output, label + ".png"))) encoder.Save(file);
        }

        private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
