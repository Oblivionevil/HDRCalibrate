using System;

namespace HDRCalibrate.Core
{
    public static class HdrToneCurve
    {
        private const double M1 = 2610.0 / 16384.0;
        private const double M2 = 2523.0 / 32.0;
        private const double C1 = 3424.0 / 4096.0;
        private const double C2 = 2413.0 / 128.0;
        private const double C3 = 2392.0 / 128.0;

        public static double[] BuildLut(
            CalibrationSettings settings,
            int entryCount,
            double sdrWhiteNits,
            double peakNits)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            settings.Validate();
            if (entryCount < 2 || entryCount > 4096)
                throw new ArgumentOutOfRangeException(nameof(entryCount));

            var values = new double[entryCount];
            for (var index = 0; index < entryCount; index++)
            {
                var input = (double)index / (entryCount - 1);
                values[index] = Evaluate(settings, input, sdrWhiteNits, peakNits);
            }

            // A display LUT must remain monotonic. This also protects against a
            // pathological highlight-blend transition at aggressive settings.
            values[0] = Clamp01(values[0]);
            for (var index = 1; index < values.Length; index++)
            {
                values[index] = Math.Max(values[index - 1], Clamp01(values[index]));
            }

            values[values.Length - 1] = 1.0;
            return values;
        }

        public static double Evaluate(
            CalibrationSettings settings,
            double pqCode,
            double sdrWhiteNits,
            double peakNits)
        {
            var input = Clamp01(pqCode);
            var safeSdrWhite = Clamp(sdrWhiteNits, 80.0, 500.0);
            var safePeak = Clamp(peakNits, safeSdrWhite, 10000.0);
            var pivot = PqEncodeNits(safeSdrWhite);

            var adjusted = settings.ApplySdrGammaCorrection
                ? ApplySdrTargetGamma(input, safeSdrWhite, settings.Gamma)
                : input;
            adjusted = pivot + (adjusted - pivot) * settings.Contrast;
            adjusted += settings.Brightness * 0.25;
            adjusted = Clamp01(adjusted);

            if (settings.ProtectHighlights)
            {
                var blendStart = pivot;
                var blendEnd = PqEncodeNits(Math.Min(safePeak, Math.Max(safeSdrWhite * 4.0, safeSdrWhite + 100.0)));
                if (blendEnd > blendStart && input > blendStart)
                {
                    var t = Clamp01((input - blendStart) / (blendEnd - blendStart));
                    var smooth = t * t * (3.0 - 2.0 * t);
                    adjusted = adjusted * (1.0 - smooth) + input * smooth;
                }
            }

            return Clamp01(adjusted);
        }

        public static double PqEncodeNits(double nits)
        {
            var normalized = Clamp(nits / 10000.0, 0.0, 1.0);
            var powered = Math.Pow(normalized, M1);
            return Math.Pow((C1 + C2 * powered) / (1.0 + C3 * powered), M2);
        }

        public static double PqDecodeNits(double code)
        {
            var powered = Math.Pow(Clamp01(code), 1.0 / M2);
            var numerator = Math.Max(powered - C1, 0.0);
            var denominator = C2 - C3 * powered;
            if (denominator <= 0.0) return 10000.0;
            return 10000.0 * Math.Pow(numerator / denominator, 1.0 / M1);
        }

        public static double SrgbToLinear(double encoded)
        {
            var value = Clamp01(encoded);
            return value <= 0.04045
                ? value / 12.92
                : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        public static double LinearToSrgb(double linear)
        {
            var value = Clamp01(linear);
            return value <= 0.0031308
                ? value * 12.92
                : 1.055 * Math.Pow(value, 1.0 / 2.4) - 0.055;
        }

        private static double ApplySdrTargetGamma(double pqCode, double sdrWhiteNits, double targetGamma)
        {
            var luminanceNits = PqDecodeNits(pqCode);
            if (luminanceNits >= sdrWhiteNits) return pqCode;

            // Windows decodes ordinary SDR content with the piecewise sRGB transfer
            // curve before composing it into the HDR scRGB desktop. Recover the
            // original SDR code value, then map it to the selected power gamma.
            var sourceLinear = Clamp01(luminanceNits / sdrWhiteNits);
            var sourceSrgbCode = LinearToSrgb(sourceLinear);
            var targetLinear = Math.Pow(sourceSrgbCode, targetGamma);
            return PqEncodeNits(targetLinear * sdrWhiteNits);
        }

        private static double Clamp01(double value) => Clamp(value, 0.0, 1.0);

        private static double Clamp(double value, double minimum, double maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }
    }
}
