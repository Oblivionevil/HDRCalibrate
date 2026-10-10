using Microsoft.Win32;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;

namespace HDRCalibrate.Setup.Core
{
    /// <summary>
    /// Starts "&lt;command&gt; --exit-existing" (the app's own graceful single-instance
    /// exit path) and then proves that the target executable is gone. A timeout
    /// fails closed; HDR Calibrate is never force-terminated.
    /// </summary>
    public sealed class ProcessAppStopper : IAppStopper
    {
        public const int ErrorInvalidParameter = 87;
        public const int ErrorBusy = 170;
        public const int ErrorTimeout = 1460;
        // The app returns 5 when the running instance did not exit within 30 s.
        public const int AppExitTimeout = 5;
        private const int CommandTimeoutMilliseconds = 35000;
        private const int TargetExitGraceMilliseconds = 5000;

        public int Stop(string commandExecutable, string targetExecutable)
        {
            string commandPath;
            string targetPath;
            try
            {
                commandPath = Path.GetFullPath(commandExecutable);
                targetPath = Path.GetFullPath(targetExecutable);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return ErrorInvalidParameter;
            }
            if (!File.Exists(commandPath)) return ErrorInvalidParameter;

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = commandPath,
                    Arguments = "--exit-existing",
                    WorkingDirectory = Path.GetDirectoryName(commandPath) ?? string.Empty,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (var process = Process.Start(startInfo))
                {
                    if (process == null) return ErrorInvalidParameter;
                    if (!process.WaitForExit(CommandTimeoutMilliseconds)) return ErrorTimeout;
                    if (process.ExitCode != 0) return process.ExitCode;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is Win32Exception)
            {
                return ErrorInvalidParameter;
            }

            var deadline = DateTime.UtcNow.AddMilliseconds(TargetExitGraceMilliseconds);
            do
            {
                if (!IsTargetRunning(targetPath)) return 0;
                Thread.Sleep(100);
            }
            while (DateTime.UtcNow < deadline);
            return ErrorBusy;
        }

        public static bool IsTargetRunning(string targetPath)
        {
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(targetPath)))
            {
                using (process)
                {
                    try
                    {
                        var runningPath = process.MainModule?.FileName;
                        if (runningPath != null && string.Equals(Path.GetFullPath(runningPath), targetPath,
                                StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    catch (InvalidOperationException) { }
                    catch (Win32Exception) { }
                }
            }
            return false;
        }
    }

    public sealed class CurrentUserRegistry : ISetupRegistry
    {
        private static RegistryKey OpenRoot() => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);

        public string GetString(string keyPath, string valueName)
        {
            using (var root = OpenRoot())
            using (var key = root.OpenSubKey(keyPath, false))
                return key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }

        public bool KeyExists(string keyPath)
        {
            using (var root = OpenRoot())
            using (var key = root.OpenSubKey(keyPath, false))
                return key != null;
        }

        public void SetString(string keyPath, string valueName, string value)
        {
            using (var root = OpenRoot())
            using (var key = root.CreateSubKey(keyPath, true))
                key.SetValue(valueName, value, RegistryValueKind.String);
        }

        public void SetDword(string keyPath, string valueName, int value)
        {
            using (var root = OpenRoot())
            using (var key = root.CreateSubKey(keyPath, true))
                key.SetValue(valueName, value, RegistryValueKind.DWord);
        }

        public void DeleteValue(string keyPath, string valueName)
        {
            using (var root = OpenRoot())
            using (var key = root.OpenSubKey(keyPath, true))
                key?.DeleteValue(valueName, false);
        }

        public void DeleteKey(string keyPath)
        {
            using (var root = OpenRoot())
                root.DeleteSubKey(keyPath, false);
        }
    }

    public sealed class ShellShortcutWriter : IShortcutWriter
    {
        public void Create(string linkPath, string targetPath, string workingDirectory, string iconPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath));
            var link = (IShellLinkW)new ShellLink();
            try
            {
                link.SetPath(targetPath);
                link.SetWorkingDirectory(workingDirectory);
                link.SetIconLocation(iconPath, 0);
                ((IPersistFile)link).Save(linkPath, true);
            }
            finally
            {
                Marshal.FinalReleaseComObject(link);
            }
        }

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, IntPtr findData, uint flags);
            void GetIDList(out IntPtr idList);
            void SetIDList(IntPtr idList);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int maxPath);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maxPath);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
            void GetHotkey(out short hotkey);
            void SetHotkey(short hotkey);
            void GetShowCmd(out int showCommand);
            void SetShowCmd(int showCommand);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxPath, out int iconIndex);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, uint reserved);
            void Resolve(IntPtr window, uint flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
        }
    }

    public sealed class FileSetupLog : ISetupLog
    {
        private readonly object _gate = new object();

        public FileSetupLog(string directory, DateTime now)
        {
            Path = System.IO.Path.Combine(directory,
                "HDRCalibrate-Setup-" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".log");
        }

        public string Path { get; }

        public void Write(string message)
        {
            lock (_gate)
            {
                try
                {
                    File.AppendAllText(Path, DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
                        + "  " + message + "\r\n", new UTF8Encoding(false));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
        }
    }

    public static class DotNetRuntime
    {
        public static bool Is481OrLaterInstalled()
        {
            try
            {
                using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var key = machine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full", false))
                    return key?.GetValue("Release") is int release && release >= ProductInfo.DotNet481Release;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                return false;
            }
        }
    }
}
