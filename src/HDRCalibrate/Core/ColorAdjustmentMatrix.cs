namespace HDRCalibrate.Core
{
    public static class ColorAdjustmentMatrix
    {
        private static readonly Matrix3x3 Bt2020RgbToXyz = new Matrix3x3(
            0.6369580483, 0.1446169036, 0.1688809752,
            0.2627002120, 0.6779980715, 0.0593017165,
            0.0000000000, 0.0280726930, 1.0609850577);

        private static readonly Matrix3x3 Bt2020XyzToRgb = Bt2020RgbToXyz.Inverse();

        public static Matrix3x3 CreateXyzAdjustment(CalibrationSettings settings)
        {
            settings.Validate();

            const double redLuma = 0.2627002120;
            const double greenLuma = 0.6779980715;
            const double blueLuma = 0.0593017165;
            var saturation = settings.Saturation;
            var inverse = 1.0 - saturation;

            var rgbSaturation = new Matrix3x3(
                inverse * redLuma + saturation, inverse * greenLuma, inverse * blueLuma,
                inverse * redLuma, inverse * greenLuma + saturation, inverse * blueLuma,
                inverse * redLuma, inverse * greenLuma, inverse * blueLuma + saturation);

            var gains = new Matrix3x3(
                settings.RedGain, 0, 0,
                0, settings.GreenGain, 0,
                0, 0, settings.BlueGain);

            var rgbAdjustment = gains.Multiply(rgbSaturation);
            return Bt2020RgbToXyz.Multiply(rgbAdjustment).Multiply(Bt2020XyzToRgb);
        }
    }
}
