using System;

namespace HDRCalibrate.Core
{
    public static class SdrAdjustmentMath
    {
        public static double[] EvaluateRgb(CalibrationSettings settings, double red, double green, double blue)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            settings.Validate();
            return new[]
            {
                Decode(settings, red),
                Decode(settings, green),
                Decode(settings, blue)
            };
        }

        public static double EvaluateGray(CalibrationSettings settings, double encoded)
        {
            return EvaluateRgb(settings, encoded, encoded, encoded)[0];
        }

        public static double SrgbToLinear(double encoded)
        {
            var value = Clamp01(encoded);
            return value <= 0.04045
                ? value / 12.92
                : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        private static double Decode(CalibrationSettings settings, double encoded)
        {
            var value = Clamp01(encoded);
            if (!settings.ApplySdrGammaCorrection) return SrgbToLinear(value);

            // Matches the live constant patch exactly: its scale is chosen so that
            // pow(encoded * scale, gamma) == pow(encoded, gamma) * (1 + brightness).
            return Clamp01(Math.Pow(value, settings.Gamma) * (1.0 + settings.Brightness));
        }

        private static double Clamp01(double value)
        {
            return Math.Max(0.0, Math.Min(1.0, value));
        }
    }
}
