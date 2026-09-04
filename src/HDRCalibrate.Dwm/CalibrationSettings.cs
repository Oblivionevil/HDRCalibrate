using System;

namespace HDRCalibrate.Core
{
    [Serializable]
    public sealed class CalibrationSettings
    {
        public double Gamma { get; set; } = 2.2;
        public bool ApplySdrGammaCorrection { get; set; } = false;
        public double Brightness { get; set; } = 0.0;
        public double Contrast { get; set; } = 1.0;
        public double Saturation { get; set; } = 1.0;
        public double RedGain { get; set; } = 1.0;
        public double GreenGain { get; set; } = 1.0;
        public double BlueGain { get; set; } = 1.0;

        // Kept only so state.xml files written by the old MHC2 implementation
        // continue to deserialize. The DWM SDR path never reads this value.
        public bool ProtectHighlights { get; set; } = true;

        public CalibrationSettings Clone()
        {
            return (CalibrationSettings)MemberwiseClone();
        }

        public void Validate()
        {
            RequireRange(nameof(Gamma), Gamma, 1.80, 2.60);
            RequireRange(nameof(Brightness), Brightness, -0.20, 0.20);
            RequireRange(nameof(Contrast), Contrast, 0.50, 1.50);
            RequireRange(nameof(Saturation), Saturation, 0.0, 2.0);
            RequireRange(nameof(RedGain), RedGain, 0.50, 1.50);
            RequireRange(nameof(GreenGain), GreenGain, 0.50, 1.50);
            RequireRange(nameof(BlueGain), BlueGain, 0.50, 1.50);
        }

        public bool IsNeutral(double epsilon = 0.000001)
        {
            return !ApplySdrGammaCorrection
                && Math.Abs(Brightness) < epsilon
                && Math.Abs(Contrast - 1.0) < epsilon
                && Math.Abs(Saturation - 1.0) < epsilon
                && Math.Abs(RedGain - 1.0) < epsilon
                && Math.Abs(GreenGain - 1.0) < epsilon
                && Math.Abs(BlueGain - 1.0) < epsilon;
        }

        private static void RequireRange(string name, double value, double minimum, double maximum)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < minimum || value > maximum)
            {
                throw new ArgumentOutOfRangeException(name, value, $"Erlaubter Bereich: {minimum} bis {maximum}.");
            }
        }
    }
}
