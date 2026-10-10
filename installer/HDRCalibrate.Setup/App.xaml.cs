using HDRCalibrate.Setup.Core;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;

namespace HDRCalibrate.Setup
{
    public partial class App : Application
    {
        private static readonly Regex TempDirectoryName =
            new Regex(@"\A" + Regex.Escape(ProductInfo.TempDirectoryPrefix) + @"[0-9a-f]{32}\z", RegexOptions.CultureInvariant);
        private Mutex _setupMutex;
        private bool _ownsMutex;
        private SetupWindow _window;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            var text = SetupText.ForCurrentCulture();
            if (!SetupCommandLine.TryParse(e.Args, out var mode))
            {
                ShowError(text, text.InvalidArguments);
                Shutdown(87);
                return;
            }
            if (!DotNetRuntime.Is481OrLaterInstalled())
            {
                ShowError(text, text.DotNetMissing);
                Shutdown(1);
                return;
            }

            var locations = SetupLocations.FromSystem();
            var log = new FileSetupLog(locations.TempDirectory, DateTime.Now);
            var context = new SetupContext(locations, new CurrentUserRegistry(), new ShellShortcutWriter(),
                new ProcessAppStopper(), text, log);
            var self = Process.GetCurrentProcess().MainModule.FileName;
            var existing = InstallationLocator.Find(context);

            // The registered uninstaller lives inside the installation. Continue from a
            // temporary copy so every installed file can be replaced or removed.
            if (InstallationLocator.SamePath(Path.GetDirectoryName(self), existing.Directory))
            {
                try
                {
                    RelaunchFromTemp(self, e.Args, locations.TempDirectory);
                    Shutdown(0);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException
                    || ex is System.ComponentModel.Win32Exception)
                {
                    log.Write("Start aus temporärer Kopie fehlgeschlagen: " + ex);
                    ShowError(text, (mode == SetupMode.Uninstall ? text.UninstallFailed(ex.Message) : text.InstallFailed(ex.Message)));
                    Shutdown(1);
                }
                return;
            }

            _setupMutex = new Mutex(false, ProductInfo.SetupMutexName);
            try { _ownsMutex = _setupMutex.WaitOne(0); }
            catch (AbandonedMutexException) { _ownsMutex = true; }
            if (!_ownsMutex)
            {
                ShowError(text, text.AlreadyRunning);
                Shutdown(1);
                return;
            }
            DeleteStaleTempDirectories(locations.TempDirectory, Path.GetDirectoryName(self), log);

            PayloadPackage package = null;
            try { package = PayloadPackage.FromAssembly(typeof(App).Assembly); }
            catch (Exception ex) when (ex is InvalidDataException || ex is System.Xml.XmlException)
            {
                log.Write("Eingebettetes Paket ungültig: " + ex.Message);
            }

            ShutdownMode = ShutdownMode.OnMainWindowClose;
            _window = new SetupWindow(mode, context, existing, package, self, log.Path);
            MainWindow = _window;
            _window.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_window != null) e.ApplicationExitCode = _window.ExitCode;
            if (_ownsMutex) _setupMutex.ReleaseMutex();
            _setupMutex?.Dispose();
            base.OnExit(e);
        }

        private static void RelaunchFromTemp(string self, string[] args, string tempRoot)
        {
            var directory = Path.Combine(tempRoot, ProductInfo.TempDirectoryPrefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var copy = Path.Combine(directory, ProductInfo.SetupExeName);
            File.Copy(self, copy, false);
            var startInfo = new ProcessStartInfo
            {
                FileName = copy,
                // Arguments were validated: none or exactly --uninstall.
                Arguments = string.Join(" ", args),
                WorkingDirectory = directory,
                UseShellExecute = false
            };
            using (Process.Start(startInfo)) { }
        }

        /// <summary>Temporary copies and staging folders of earlier runs; never the running copy.</summary>
        private static void DeleteStaleTempDirectories(string tempRoot, string ownDirectory, ISetupLog log)
        {
            try
            {
                foreach (var directory in Directory.GetDirectories(tempRoot, ProductInfo.TempDirectoryPrefix + "*"))
                {
                    if (!TempDirectoryName.IsMatch(Path.GetFileName(directory))
                        || InstallationLocator.SamePath(directory, ownDirectory) || SetupPaths.IsReparsePoint(directory))
                        continue;
                    try { Directory.Delete(directory, true); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        log.Write("Alter temporärer Setup-Ordner noch in Verwendung: " + directory);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        private static void ShowError(SetupText text, string message) =>
            MessageBox.Show(message, text.WindowTitle(typeof(App).Assembly.GetName().Version.ToString(3)),
                MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
