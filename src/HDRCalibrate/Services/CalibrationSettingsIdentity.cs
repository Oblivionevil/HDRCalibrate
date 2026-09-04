using HDRCalibrate.Core;
using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace HDRCalibrate.Services
{
    public static class CalibrationSettingsIdentity
    {
        public static string Compute(CalibrationSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            settings.Validate();
            var canonical = string.Join("|", new[]
            {
                settings.ApplySdrGammaCorrection ? "1" : "0",
                settings.Gamma.ToString("R", CultureInfo.InvariantCulture),
                settings.Brightness.ToString("R", CultureInfo.InvariantCulture),
                settings.Contrast.ToString("R", CultureInfo.InvariantCulture),
                settings.Saturation.ToString("R", CultureInfo.InvariantCulture),
                settings.RedGain.ToString("R", CultureInfo.InvariantCulture),
                settings.GreenGain.ToString("R", CultureInfo.InvariantCulture),
                settings.BlueGain.ToString("R", CultureInfo.InvariantCulture),
                settings.ProtectHighlights ? "1" : "0"
            });
            using (var sha256 = SHA256.Create())
                return BitConverter.ToString(sha256.ComputeHash(Encoding.UTF8.GetBytes(canonical)))
                    .Replace("-", string.Empty);
        }
    }
}
