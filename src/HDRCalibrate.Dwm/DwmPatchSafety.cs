using System;

namespace HDRCalibrate.Dwm
{
    public static class DwmPatchSafety
    {
        public const string BlockReason =
            "Dauerhaftes Anwenden ist in diesem Build deaktiviert.";

        public const string TimedTestBlockReason =
            "Der überwachte DWM-Test ist in diesem Build deaktiviert.";

        public static bool LivePatchEnabled => true;
        public static bool TimedTestEnabled => true;

        public static void EnsureLivePatchEnabled()
        {
            if (!LivePatchEnabled) throw new NotSupportedException(BlockReason);
        }

        public static void EnsureTimedTestEnabled()
        {
            if (!TimedTestEnabled) throw new NotSupportedException(TimedTestBlockReason);
        }
    }
}
