using System;
using System.Collections.Generic;

namespace HDRCalibrate.Services
{
    public static class StartupOptions
    {
        public const string ExitExistingArgument = "--exit-existing";

        public static bool ShouldStartInTray(IEnumerable<string> arguments)
        {
            return Contains(arguments, AutostartService.TrayArgument);
        }

        public static bool ShouldExitExisting(IEnumerable<string> arguments)
        {
            return Contains(arguments, ExitExistingArgument);
        }

        public static bool ShouldActivateAtStartup(IEnumerable<string> arguments)
        {
            return Contains(arguments, AutostartService.StartupArgument);
        }

        private static bool Contains(IEnumerable<string> arguments, string expected)
        {
            if (arguments == null) return false;
            foreach (var argument in arguments)
            {
                if (string.Equals(argument, expected, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
