using HDRCalibrate.Setup;
using HDRCalibrate.Setup.Core;
using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace HDRCalibrate.Tests
{
    /// <summary>
    /// WPF allows one Application per AppDomain and the program-list UI test already
    /// owns this domain's instance, so the setup window is checked in its own domain.
    /// Nothing is installed: the window is only constructed and laid out.
    /// </summary>
    internal static class SetupUiTests
    {
        public static void WindowLoadsSharedThemeInBothModes()
        {
            var domain = AppDomain.CreateDomain("HDRCalibrate.Setup.UiTest", null,
                new AppDomainSetup { ApplicationBase = AppDomain.CurrentDomain.BaseDirectory });
            try
            {
                var runner = (Runner)domain.CreateInstanceAndUnwrap(typeof(Runner).Assembly.FullName, typeof(Runner).FullName);
                var error = runner.Run();
                if (error != null) throw new InvalidOperationException("Setup-Fenster: " + error);
            }
            finally
            {
                AppDomain.Unload(domain);
            }
        }

        public sealed class Runner : MarshalByRefObject
        {
            public string Run()
            {
                string error = null;
                var thread = new Thread(() =>
                {
                    try { Check(); }
                    catch (Exception ex) { error = ex.ToString(); }
                });
                thread.IsBackground = true;
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                return thread.Join(20000) ? error : "Zeitbudget überschritten.";
            }

            private static void Check()
            {
                var application = new App();
                application.InitializeComponent();
                using (var sandbox = new SetupTests.Sandbox())
                {
                    var package = SetupTests.Package(SetupTests.PackageFiles());
                    var existing = InstallationLocator.Find(sandbox.Context);
                    var install = new SetupWindow(SetupMode.Install, sandbox.Context, existing, package, sandbox.SetupExecutable, "setup.log");
                    try
                    {
                        Require(install.Icon != null && install.Title.Contains("1.7.3"), "Icon or title missing.");
                        Require(Text(install, "HeadingText") == "HDR Calibrate installieren"
                            && (string)((Button)install.FindName("PrimaryButton")).Content == "Installieren"
                            && ((CheckBox)install.FindName("AutostartCheck")).IsChecked == false
                            && ((CheckBox)install.FindName("DesktopCheck")).IsChecked == false,
                            "Fresh install options are wrong.");
                        Require(Text(install, "TargetText").EndsWith(sandbox.Install, StringComparison.Ordinal),
                            "The fixed per-user folder is not shown.");
                        var content = (FrameworkElement)install.Content;
                        content.Measure(new Size(640, 440));
                        content.Arrange(new Rect(0, 0, 640, 440));
                        content.UpdateLayout();
                        var primary = (FrameworkElement)install.FindName("PrimaryButton");
                        var bounds = primary.TransformToAncestor(content).TransformBounds(new Rect(0, 0, primary.ActualWidth, primary.ActualHeight));
                        Require(bounds.Right <= 641 && bounds.Bottom <= 441 && primary.ActualWidth > 0, "The primary button is clipped.");
                    }
                    finally { install.Close(); }

                    var missing = new SetupWindow(SetupMode.Install, sandbox.Context, existing, null, sandbox.SetupExecutable, null);
                    try
                    {
                        Require(Text(missing, "ResultText") == sandbox.Context.Text.PayloadMissing
                            && missing.ExitCode == 1, "A setup without payload offered installation.");
                    }
                    finally { missing.Close(); }

                    var uninstall = new SetupWindow(SetupMode.Uninstall, sandbox.Context, existing, package, sandbox.SetupExecutable, null);
                    try
                    {
                        Require(Text(uninstall, "ResultText") == sandbox.Context.Text.NotInstalled,
                            "Uninstall offered to remove a missing installation.");
                    }
                    finally { uninstall.Close(); }
                }
                application.Shutdown();
            }

            private static string Text(Window window, string name) => ((TextBlock)window.FindName(name)).Text;

            private static void Require(bool condition, string message)
            {
                if (!condition) throw new InvalidOperationException(message);
            }
        }
    }
}
