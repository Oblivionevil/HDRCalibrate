using HDRCalibrate.Dwm;
using System;
using System.Diagnostics;

namespace HDRCalibrate.Services
{
    /// <summary>
    /// The selective native hook has a private ABI contract independent of the
    /// global SDR shader manifest. Never infer its support from DwmCoreInspector.
    /// These pins must match the separately reviewed native BuildContract.h and
    /// Controller/Preflight.cpp; a new global shader build does not extend them.
    /// </summary>
    public static class ProgramGammaBuildSupport
    {
        public const string SupportedDwmCoreVersion = "10.0.26100.8875 (WinBuild.160101.0800)";
        public const string SupportedDwmCoreSha256 = "825726F26BD6B5A02B49F6145963B54D537D3D1239055DDA28D1FB0B3E864EF3";
        public const string UnsupportedMessage =
            "Gamma-Erzwingen für einzelne Programme ist für diese Windows-DWM-Version noch nicht freigegeben. " +
            "Die globale SDR-Korrektur wird dadurch nicht deaktiviert.";

        public static bool IsSupportedVersion(string version) =>
            string.Equals(version, SupportedDwmCoreVersion, StringComparison.Ordinal);

        public static bool IsSupported(string version, string sha256) =>
            IsSupportedVersion(version)
            && string.Equals(sha256, SupportedDwmCoreSha256, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Cheap advisory version check for the UI. The runtime gate still verifies
        /// the exact version and hash; an unreadable file is left to that gate.
        /// </summary>
        public static string GetSystemUnavailableReason()
        {
            try
            {
                var version = FileVersionInfo.GetVersionInfo(DwmCoreInspector.GetSystemDwmCorePath()).FileVersion;
                return IsSupportedVersion(version) ? null : UnsupportedMessage;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static void RequireSupported(string version, string sha256)
        {
            if (!IsSupported(version, sha256)) throw new NotSupportedException(UnsupportedMessage);
        }
    }
}
