using System;

namespace HDRCalibrate.Core
{
    [Serializable]
    public sealed class DisplayColorMetadata
    {
        public double RedX { get; set; } = 0.708;
        public double RedY { get; set; } = 0.292;
        public double GreenX { get; set; } = 0.170;
        public double GreenY { get; set; } = 0.797;
        public double BlueX { get; set; } = 0.131;
        public double BlueY { get; set; } = 0.046;
        public double WhiteX { get; set; } = 0.3127;
        public double WhiteY { get; set; } = 0.3290;
        public double MinLuminanceNits { get; set; } = 0.001;
        public double MaxLuminanceNits { get; set; } = 1000.0;
        public double MaxFullFrameLuminanceNits { get; set; } = 400.0;

        public bool IsUsable()
        {
            return IsChromaticity(RedX, RedY)
                && IsChromaticity(GreenX, GreenY)
                && IsChromaticity(BlueX, BlueY)
                && IsChromaticity(WhiteX, WhiteY)
                && MaxLuminanceNits >= 80.0
                && MaxLuminanceNits <= 10000.0
                && MaxFullFrameLuminanceNits > 0.0
                && MaxFullFrameLuminanceNits <= MaxLuminanceNits * 1.1
                && MinLuminanceNits >= 0.0
                && MinLuminanceNits < MaxLuminanceNits;
        }

        public DisplayColorMetadata Clone()
        {
            return (DisplayColorMetadata)MemberwiseClone();
        }

        private static bool IsChromaticity(double x, double y)
        {
            return x > 0.0 && y > 0.0 && x < 1.0 && y < 1.0 && x + y <= 1.0001;
        }
    }
}
