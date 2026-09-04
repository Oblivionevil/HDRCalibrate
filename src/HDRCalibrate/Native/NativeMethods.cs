using System;
using System.Runtime.InteropServices;
using System.Text;

namespace HDRCalibrate.Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Luid : IEquatable<Luid>
    {
        public uint LowPart;
        public int HighPart;

        public long Value => ((long)HighPart << 32) | LowPart;

        public bool Equals(Luid other) => LowPart == other.LowPart && HighPart == other.HighPart;
        public override bool Equals(object obj) => obj is Luid other && Equals(other);
        public override int GetHashCode() => unchecked(((int)LowPart * 397) ^ HighPart);
        public override string ToString() => $"{unchecked((uint)HighPart):X8}{LowPart:X8}";
    }

    internal enum DisplayConfigDeviceInfoType : uint
    {
        GetSourceName = 1,
        GetTargetName = 2,
        GetAdvancedColorInfo = 9,
        GetSdrWhiteLevel = 11,
        GetAdvancedColorInfo2 = 15
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayConfigDeviceInfoHeader
    {
        public DisplayConfigDeviceInfoType Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayConfigPathSourceInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayConfigRational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayConfigPathTargetInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public DisplayConfigRational RefreshRate;
        public uint ScanLineOrdering;
        [MarshalAs(UnmanagedType.Bool)] public bool TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayConfigPathInfo
    {
        public DisplayConfigPathSourceInfo SourceInfo;
        public DisplayConfigPathTargetInfo TargetInfo;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Explicit, Size = 48)]
    internal struct DisplayConfigModeUnion
    {
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayConfigModeInfo
    {
        public uint InfoType;
        public uint Id;
        public Luid AdapterId;
        public DisplayConfigModeUnion Mode;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DisplayConfigSourceDeviceName
    {
        public DisplayConfigDeviceInfoHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string ViewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DisplayConfigTargetDeviceName
    {
        public DisplayConfigDeviceInfoHeader Header;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string MonitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string MonitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayConfigAdvancedColorInfo2
    {
        public DisplayConfigDeviceInfoHeader Header;
        public uint Flags;
        public uint ColorEncoding;
        public uint BitsPerColorChannel;
        public uint ActiveColorMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayConfigAdvancedColorInfo
    {
        public DisplayConfigDeviceInfoHeader Header;
        public uint Flags;
        public uint ColorEncoding;
        public uint BitsPerColorChannel;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayConfigSdrWhiteLevel
    {
        public DisplayConfigDeviceInfoHeader Header;
        public uint SdrWhiteLevel;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WcsDeviceMhc2Capabilities
    {
        public uint Size;
        [MarshalAs(UnmanagedType.Bool)] public bool SupportsMhc2;
        public uint RegammaLutEntryCount;
        public uint CscXyzMatrixRows;
        public uint CscXyzMatrixColumns;
    }

    internal enum WcsProfileManagementScope
    {
        SystemWide = 0,
        CurrentUser = 1
    }

    internal enum ColorProfileType
    {
        Icc = 0
    }

    internal enum ColorProfileSubType
    {
        StandardDisplayColorMode = 7,
        ExtendedDisplayColorMode = 8
    }

    internal enum WcsDeviceCapabilitiesType
    {
        MicrosoftHardwareColorV2 = 2
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ColorProfileDescriptor
    {
        public uint Type;
        public IntPtr ProfileData;
        public uint DataSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DxgiOutputDescription1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        [MarshalAs(UnmanagedType.Bool)] public bool AttachedToDesktop;
        public uint Rotation;
        public IntPtr Monitor;
        public uint BitsPerColor;
        public int ColorSpace;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] public float[] RedPrimary;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] public float[] GreenPrimary;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] public float[] BluePrimary;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] public float[] WhitePoint;
        public float MinLuminance;
        public float MaxLuminance;
        public float MaxFullFrameLuminance;
    }

    internal static class NativeMethods
    {
        internal const int ErrorSuccess = 0;
        internal const int ErrorInsufficientBuffer = 122;
        internal const uint QdcOnlyActivePaths = 0x00000002;
        internal const int DxgiErrorNotFound = unchecked((int)0x887A0002);

        [DllImport("user32.dll")]
        internal static extern int GetDisplayConfigBufferSizes(
            uint flags,
            out uint numPathArrayElements,
            out uint numModeInfoArrayElements);

        [DllImport("user32.dll")]
        internal static extern int QueryDisplayConfig(
            uint flags,
            ref uint numPathArrayElements,
            [In, Out] DisplayConfigPathInfo[] pathInfoArray,
            ref uint numModeInfoArrayElements,
            [In, Out] DisplayConfigModeInfo[] modeInfoArray,
            IntPtr currentTopologyId);

        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
        internal static extern int DisplayConfigGetSourceName(ref DisplayConfigSourceDeviceName request);

        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
        internal static extern int DisplayConfigGetTargetName(ref DisplayConfigTargetDeviceName request);

        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
        internal static extern int DisplayConfigGetAdvancedColorInfo2(ref DisplayConfigAdvancedColorInfo2 request);

        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
        internal static extern int DisplayConfigGetAdvancedColorInfo(ref DisplayConfigAdvancedColorInfo request);

        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
        internal static extern int DisplayConfigGetSdrWhiteLevel(ref DisplayConfigSdrWhiteLevel request);

        [DllImport("mscms.dll")]
        internal static extern int ColorProfileGetDeviceCapabilities(
            WcsProfileManagementScope scope,
            Luid targetAdapterId,
            uint sourceId,
            WcsDeviceCapabilitiesType capsType,
            ref WcsDeviceMhc2Capabilities outputCapabilities);

        [DllImport("mscms.dll")]
        internal static extern int ColorProfileGetDisplayUserScope(
            Luid targetAdapterId,
            uint sourceId,
            out WcsProfileManagementScope scope);

        [DllImport("mscms.dll", CharSet = CharSet.Unicode)]
        internal static extern int ColorProfileGetDisplayDefault(
            WcsProfileManagementScope scope,
            Luid targetAdapterId,
            uint sourceId,
            ColorProfileType profileType,
            ColorProfileSubType profileSubType,
            out IntPtr profileName);

        [DllImport("mscms.dll", CharSet = CharSet.Unicode)]
        internal static extern int ColorProfileAddDisplayAssociation(
            WcsProfileManagementScope scope,
            string profileName,
            Luid targetAdapterId,
            uint sourceId,
            [MarshalAs(UnmanagedType.Bool)] bool setAsDefault,
            [MarshalAs(UnmanagedType.Bool)] bool associateAsAdvancedColor);

        [DllImport("mscms.dll", CharSet = CharSet.Unicode)]
        internal static extern int ColorProfileRemoveDisplayAssociation(
            WcsProfileManagementScope scope,
            string profileName,
            Luid targetAdapterId,
            uint sourceId,
            [MarshalAs(UnmanagedType.Bool)] bool disassociateAdvancedColor);

        [DllImport("mscms.dll", CharSet = CharSet.Unicode)]
        internal static extern int ColorProfileSetDisplayDefaultAssociation(
            WcsProfileManagementScope scope,
            string profileName,
            ColorProfileType profileType,
            ColorProfileSubType profileSubType,
            Luid targetAdapterId,
            uint sourceId);

        [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetColorDirectory(
            string machineName,
            StringBuilder buffer,
            ref uint size);

        [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool InstallColorProfile(string machineName, string profilePath);

        [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UninstallColorProfile(string machineName, string profileName, [MarshalAs(UnmanagedType.Bool)] bool delete);

        [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr OpenColorProfile(
            ref ColorProfileDescriptor profile,
            uint desiredAccess,
            uint shareMode,
            uint creationMode);

        [DllImport("mscms.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsColorProfileValid(IntPtr profile, [MarshalAs(UnmanagedType.Bool)] out bool valid);

        [DllImport("mscms.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseColorProfile(IntPtr profile);

        [DllImport("kernel32.dll")]
        internal static extern IntPtr LocalFree(IntPtr memory);

        [DllImport("dxgi.dll")]
        internal static extern int CreateDXGIFactory1(ref Guid requestedInterface, out IntPtr factory);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int EnumAdapters1Delegate(IntPtr factory, uint adapterIndex, out IntPtr adapter);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int EnumOutputsDelegate(IntPtr adapter, uint outputIndex, out IntPtr output);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int GetDesc1Delegate(IntPtr output, out DxgiOutputDescription1 description);

        internal static T GetComMethod<T>(IntPtr instance, int slot) where T : class
        {
            var vtable = Marshal.ReadIntPtr(instance);
            var method = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer(method, typeof(T)) as T;
        }
    }
}
