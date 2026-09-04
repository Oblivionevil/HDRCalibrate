using HDRCalibrate.Core;
using HDRCalibrate.Native;

namespace HDRCalibrate.Services
{
    public sealed class DisplayInfo
    {
        public string FriendlyName { get; internal set; }
        public string GdiDeviceName { get; internal set; }
        public string MonitorDevicePath { get; internal set; }
        public Luid AdapterId { get; internal set; }
        public uint SourceId { get; internal set; }
        public uint TargetId { get; internal set; }
        public bool HdrSupported { get; internal set; }
        public bool HdrUserEnabled { get; internal set; }
        public bool HdrActive { get; internal set; }
        public bool AdvancedColorLimitedByPolicy { get; internal set; }
        public uint BitsPerColorChannel { get; internal set; }
        public uint ColorEncoding { get; internal set; }
        public double SdrWhiteNits { get; internal set; }
        public bool Mhc2Supported { get; internal set; }
        public uint Mhc2LutEntries { get; internal set; }
        public string ActiveProfileName { get; internal set; }
        public string ActiveProfilePath { get; internal set; }
        internal WcsProfileManagementScope ProfileScope { get; set; }
        public DisplayColorMetadata ColorMetadata { get; internal set; }

        public string Key => $"{AdapterId}-{SourceId}-{TargetId}";
        public string DisplayLabel => $"{FriendlyName}  ·  {(HdrActive ? "HDR aktiv" : "HDR inaktiv")}";
        public string BitDepthLabel => BitsPerColorChannel == 0 ? "unbekannt" : $"{BitsPerColorChannel} Bit";
        public string SdrWhiteLabel => SdrWhiteNits <= 0 ? "unbekannt" : $"{SdrWhiteNits:0} nit";
        public string ProfileLabel => string.IsNullOrWhiteSpace(ActiveProfileName) ? "Kein zugeordnetes HDR-Profil" : ActiveProfileName;

        public override string ToString() => DisplayLabel;
    }
}
