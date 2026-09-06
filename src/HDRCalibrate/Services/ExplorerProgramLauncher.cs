using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace HDRCalibrate.Services
{
    public static class ExplorerProgramLauncher
    {
        private const int SwcDesktop = 8;
        private const int SwfoNeedDispatch = 1;

        public static void Start(ProcessStartInfo info)
        {
            if (info == null) throw new ArgumentNullException(nameof(info));
            var path = ProgramProfileRules.NormalizePath(info.FileName);
            var arguments = info.Arguments ?? string.Empty;
            if (arguments != ProgramProfileRules.Arguments(ProgramColorMode.Default)
                && arguments != ProgramProfileRules.Arguments(ProgramColorMode.Srgb)
                && arguments != ProgramProfileRules.Arguments(ProgramColorMode.ScrgbLinear))
                throw new ArgumentException("Nur die festen Farbraum-Startparameter sind erlaubt.");
            var directory = Path.GetDirectoryName(path);
            if (!string.Equals(directory, info.WorkingDirectory, StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrEmpty(info.Verb) || info.UseShellExecute)
                throw new ArgumentException("Ungültiger Programmstart für die Explorer-Übergabe.");
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("Die Explorer-Übergabe benötigt den STA-Oberflächenthread.");

            object windows = null, browser = null, view = null, shell = null;
            try
            {
                // Obtain the EXISTING desktop's automation object. Creating a
                // fresh Shell.Application object alone does not guarantee that
                // ShellExecute runs in the interactive desktop user's process.
                // The desktop may itself be elevated when UAC is disabled;
                // this is allowed and Windows security settings are not changed.
                // https://devblogs.microsoft.com/oldnewthing/20131118-00/?p=2643
                windows = Activator.CreateInstance(Type.GetTypeFromCLSID(
                    new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"), true));
                var findArgs = new object[] { 0, null, SwcDesktop, 0, SwfoNeedDispatch };
                var byRef = new ParameterModifier(5);
                byRef[0] = true;
                byRef[1] = true;
                byRef[3] = true;
                browser = Dispatch(windows, "FindWindowSW", BindingFlags.InvokeMethod, findArgs, new[] { byRef });
                if (browser == null) throw new InvalidOperationException("Der Windows-Desktop ist nicht verfügbar.");
                var window = new IntPtr(Convert.ToInt32(findArgs[3], CultureInfo.InvariantCulture));
                RequireInteractiveDesktop(window);
                view = Dispatch(browser, "Document", BindingFlags.GetProperty);
                shell = Dispatch(view, "Application", BindingFlags.GetProperty);
                RequireInteractiveDesktop(window);
                // Absolute EXE and fixed arguments are separate COM values: no
                // cmd.exe, PowerShell, command concatenation or runas verb.
                Dispatch(shell, "ShellExecute", BindingFlags.InvokeMethod,
                    new object[] { path, arguments, directory, "open", 1 });
            }
            catch (Exception ex) when (ex is COMException || ex is TargetInvocationException
                || ex is InvalidOperationException)
            {
                var cause = (ex as TargetInvocationException)?.InnerException ?? ex;
                throw new InvalidOperationException("Start über Windows-Explorer fehlgeschlagen: " + cause.Message
                    + " Es wurde kein zweiter Startversuch ausgeführt.", cause);
            }
            finally
            {
                Release(shell);
                Release(view);
                Release(browser);
                Release(windows);
            }
        }

        private static object Dispatch(object target, string member, BindingFlags operation,
            object[] arguments = null, ParameterModifier[] modifiers = null)
        {
            if (target == null) throw new InvalidOperationException("Das Desktop-Automatisierungsobjekt fehlt.");
            return target.GetType().InvokeMember(member, operation, null, target, arguments,
                modifiers, CultureInfo.InvariantCulture, null);
        }

        private static void RequireInteractiveDesktop(IntPtr window)
        {
            var desktop = GetShellWindow();
            if (window == IntPtr.Zero || desktop == IntPtr.Zero
                || GetWindowThreadProcessId(window, out var windowProcess) == 0
                || GetWindowThreadProcessId(desktop, out var desktopProcess) == 0
                || windowProcess != desktopProcess)
                throw new InvalidOperationException("Der interaktive Windows-Desktop konnte nicht bestätigt werden.");
        }

        private static void Release(object value)
        {
            if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    }
}
