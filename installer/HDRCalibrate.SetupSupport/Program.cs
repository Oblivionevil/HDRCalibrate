using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace HDRCalibrate.SetupSupport
{
    internal static class Program
    {
        private const int ErrorInvalidParameter = 87;
        private const int ErrorBusy = 170;
        private const int ErrorTimeout = 1460;
        // The app-side --exit-existing command waits up to 30 seconds for the
        // primary mutex. Give it a small transport margin before failing closed.
        private const int CommandTimeoutMilliseconds = 35000;
        private const int TargetExitGraceMilliseconds = 5000;

        private static int Main(string[] args)
        {
            if (args.Length != 2 || string.IsNullOrWhiteSpace(args[0]) || string.IsNullOrWhiteSpace(args[1]))
            {
                return ErrorInvalidParameter;
            }

            string commandPath;
            string targetPath;

            try
            {
                commandPath = Path.GetFullPath(args[0]);
                targetPath = Path.GetFullPath(args[1]);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return ErrorInvalidParameter;
            }

            if (!File.Exists(commandPath))
            {
                return ErrorInvalidParameter;
            }

            int commandResult;
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
                    if (process == null)
                    {
                        return ErrorInvalidParameter;
                    }

                    if (!process.WaitForExit(CommandTimeoutMilliseconds))
                    {
                        // Deliberately do not terminate the process. The installer must
                        // fail closed instead of force-killing HDR Calibrate.
                        return ErrorTimeout;
                    }

                    commandResult = process.ExitCode;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is Win32Exception)
            {
                return ErrorInvalidParameter;
            }

            if (commandResult != 0)
            {
                return commandResult;
            }

            var deadline = DateTime.UtcNow.AddMilliseconds(TargetExitGraceMilliseconds);
            do
            {
                if (!IsTargetRunning(targetPath))
                {
                    return 0;
                }

                Thread.Sleep(100);
            }
            while (DateTime.UtcNow < deadline);

            return ErrorBusy;
        }

        private static bool IsTargetRunning(string targetPath)
        {
            var processName = Path.GetFileNameWithoutExtension(targetPath);
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    try
                    {
                        var module = process.MainModule;
                        var runningPath = module == null ? null : module.FileName;
                        if (runningPath != null &&
                            string.Equals(
                                Path.GetFullPath(runningPath),
                                targetPath,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        // The process exited between enumeration and inspection.
                    }
                    catch (Win32Exception)
                    {
                        // An inaccessible process from another security context cannot
                        // be proven to be this per-user installation.
                    }
                }
            }

            return false;
        }
    }
}
