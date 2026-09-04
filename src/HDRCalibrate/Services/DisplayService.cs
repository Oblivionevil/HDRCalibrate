using HDRCalibrate.Core;
using HDRCalibrate.Native;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace HDRCalibrate.Services
{
    public sealed class DisplayService
    {
        public IReadOnlyList<DisplayInfo> GetActiveDisplays()
        {
            ValidateInteropLayouts();
            var paths = QueryActivePaths();
            var dxgiOutputs = DxgiOutputReader.GetOutputs();
            var displays = new List<DisplayInfo>();

            foreach (var path in paths)
            {
                var source = GetSourceName(path.SourceInfo);
                var target = GetTargetName(path.TargetInfo);
                var display = new DisplayInfo
                {
                    AdapterId = path.SourceInfo.AdapterId,
                    SourceId = path.SourceInfo.Id,
                    TargetId = path.TargetInfo.Id,
                    GdiDeviceName = source.ViewGdiDeviceName ?? string.Empty,
                    FriendlyName = string.IsNullOrWhiteSpace(target.MonitorFriendlyDeviceName)
                        ? (source.ViewGdiDeviceName ?? "Unbekannter Monitor")
                        : target.MonitorFriendlyDeviceName,
                    MonitorDevicePath = target.MonitorDevicePath ?? string.Empty,
                    SdrWhiteNits = GetSdrWhiteNits(path.TargetInfo),
                    ColorMetadata = new DisplayColorMetadata()
                };

                ReadAdvancedColor(path.TargetInfo, display);
                ReadMhcCapabilities(path.SourceInfo, display);
                ReadActiveProfile(path.SourceInfo, display);

                if (dxgiOutputs.TryGetValue(display.GdiDeviceName, out var output))
                {
                    display.ColorMetadata = output.Metadata;
                    if (display.BitsPerColorChannel == 0) display.BitsPerColorChannel = output.BitsPerColor;
                }

                displays.Add(display);
            }

            return displays.OrderByDescending(display => display.HdrActive).ThenBy(display => display.FriendlyName).ToArray();
        }

        public static bool ValidateProfileFile(string path, out string error)
        {
            error = null;
            if (!File.Exists(path))
            {
                error = "Die Profildatei existiert nicht.";
                return false;
            }

            var pointer = Marshal.StringToHGlobalUni(path);
            try
            {
                var descriptor = new ColorProfileDescriptor
                {
                    Type = 1,
                    ProfileData = pointer,
                    DataSize = checked((uint)((path.Length + 1) * 2))
                };
                var handle = NativeMethods.OpenColorProfile(ref descriptor, 1, 1, 3);
                if (handle == IntPtr.Zero)
                {
                    error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                    return false;
                }

                try
                {
                    if (!NativeMethods.IsColorProfileValid(handle, out var valid))
                    {
                        error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                        return false;
                    }
                    if (!valid) error = "Windows stuft das ICC-Profil als ungültig ein.";
                    return valid;
                }
                finally
                {
                    NativeMethods.CloseColorProfile(handle);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }

        public static string GetColorDirectory()
        {
            uint size = 0;
            NativeMethods.GetColorDirectory(null, null, ref size);
            if (size == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Der Windows-Farbprofilordner konnte nicht ermittelt werden.");
            var buffer = new StringBuilder(checked((int)size));
            if (!NativeMethods.GetColorDirectory(null, buffer, ref size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Der Windows-Farbprofilordner konnte nicht ermittelt werden.");
            return buffer.ToString();
        }

        private static DisplayConfigPathInfo[] QueryActivePaths()
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var result = NativeMethods.GetDisplayConfigBufferSizes(
                    NativeMethods.QdcOnlyActivePaths,
                    out var pathCount,
                    out var modeCount);
                if (result != NativeMethods.ErrorSuccess) throw new Win32Exception(result, "DisplayConfig-Puffer konnten nicht bestimmt werden.");

                var paths = new DisplayConfigPathInfo[pathCount];
                var modes = new DisplayConfigModeInfo[modeCount];
                result = NativeMethods.QueryDisplayConfig(
                    NativeMethods.QdcOnlyActivePaths,
                    ref pathCount,
                    paths,
                    ref modeCount,
                    modes,
                    IntPtr.Zero);
                if (result == NativeMethods.ErrorInsufficientBuffer) continue;
                if (result != NativeMethods.ErrorSuccess) throw new Win32Exception(result, "Aktive Monitorpfade konnten nicht gelesen werden.");
                return paths.Take(checked((int)pathCount)).ToArray();
            }

            throw new InvalidOperationException("Die Monitoranordnung änderte sich während der Erkennung wiederholt.");
        }

        private static DisplayConfigSourceDeviceName GetSourceName(DisplayConfigPathSourceInfo source)
        {
            var request = new DisplayConfigSourceDeviceName
            {
                Header = CreateHeader(DisplayConfigDeviceInfoType.GetSourceName, Marshal.SizeOf(typeof(DisplayConfigSourceDeviceName)), source.AdapterId, source.Id)
            };
            var result = NativeMethods.DisplayConfigGetSourceName(ref request);
            if (result != NativeMethods.ErrorSuccess) throw new Win32Exception(result, "Der GDI-Monitorname konnte nicht gelesen werden.");
            return request;
        }

        private static DisplayConfigTargetDeviceName GetTargetName(DisplayConfigPathTargetInfo target)
        {
            var request = new DisplayConfigTargetDeviceName
            {
                Header = CreateHeader(DisplayConfigDeviceInfoType.GetTargetName, Marshal.SizeOf(typeof(DisplayConfigTargetDeviceName)), target.AdapterId, target.Id)
            };
            var result = NativeMethods.DisplayConfigGetTargetName(ref request);
            if (result != NativeMethods.ErrorSuccess) throw new Win32Exception(result, "Der Monitorname konnte nicht gelesen werden.");
            return request;
        }

        private static void ReadAdvancedColor(DisplayConfigPathTargetInfo target, DisplayInfo display)
        {
            var modern = new DisplayConfigAdvancedColorInfo2
            {
                Header = CreateHeader(DisplayConfigDeviceInfoType.GetAdvancedColorInfo2, Marshal.SizeOf(typeof(DisplayConfigAdvancedColorInfo2)), target.AdapterId, target.Id)
            };
            var result = NativeMethods.DisplayConfigGetAdvancedColorInfo2(ref modern);
            if (result == NativeMethods.ErrorSuccess)
            {
                display.HdrSupported = (modern.Flags & (1u << 4)) != 0;
                display.HdrUserEnabled = (modern.Flags & (1u << 5)) != 0;
                display.AdvancedColorLimitedByPolicy = (modern.Flags & (1u << 3)) != 0;
                display.HdrActive = (modern.Flags & (1u << 1)) != 0 && modern.ActiveColorMode == 2;
                display.BitsPerColorChannel = modern.BitsPerColorChannel;
                display.ColorEncoding = modern.ColorEncoding;
                return;
            }

            var legacy = new DisplayConfigAdvancedColorInfo
            {
                Header = CreateHeader(DisplayConfigDeviceInfoType.GetAdvancedColorInfo, Marshal.SizeOf(typeof(DisplayConfigAdvancedColorInfo)), target.AdapterId, target.Id)
            };
            result = NativeMethods.DisplayConfigGetAdvancedColorInfo(ref legacy);
            if (result != NativeMethods.ErrorSuccess) return;
            display.HdrSupported = (legacy.Flags & 1u) != 0;
            display.HdrUserEnabled = (legacy.Flags & 2u) != 0;
            display.HdrActive = display.HdrUserEnabled;
            display.AdvancedColorLimitedByPolicy = (legacy.Flags & 8u) != 0;
            display.BitsPerColorChannel = legacy.BitsPerColorChannel;
            display.ColorEncoding = legacy.ColorEncoding;
        }

        private static double GetSdrWhiteNits(DisplayConfigPathTargetInfo target)
        {
            var request = new DisplayConfigSdrWhiteLevel
            {
                Header = CreateHeader(DisplayConfigDeviceInfoType.GetSdrWhiteLevel, Marshal.SizeOf(typeof(DisplayConfigSdrWhiteLevel)), target.AdapterId, target.Id)
            };
            return NativeMethods.DisplayConfigGetSdrWhiteLevel(ref request) == NativeMethods.ErrorSuccess
                ? request.SdrWhiteLevel / 1000.0 * 80.0
                : 203.0;
        }

        private static void ReadMhcCapabilities(DisplayConfigPathSourceInfo source, DisplayInfo display)
        {
            var capabilities = new WcsDeviceMhc2Capabilities
            {
                Size = (uint)Marshal.SizeOf(typeof(WcsDeviceMhc2Capabilities))
            };
            var result = NativeMethods.ColorProfileGetDeviceCapabilities(
                WcsProfileManagementScope.CurrentUser,
                source.AdapterId,
                source.Id,
                WcsDeviceCapabilitiesType.MicrosoftHardwareColorV2,
                ref capabilities);
            if (result < 0)
            {
                capabilities = new WcsDeviceMhc2Capabilities { Size = (uint)Marshal.SizeOf(typeof(WcsDeviceMhc2Capabilities)) };
                result = NativeMethods.ColorProfileGetDeviceCapabilities(
                    WcsProfileManagementScope.SystemWide,
                    source.AdapterId,
                    source.Id,
                    WcsDeviceCapabilitiesType.MicrosoftHardwareColorV2,
                    ref capabilities);
            }
            if (result >= 0)
            {
                display.Mhc2Supported = capabilities.SupportsMhc2;
                display.Mhc2LutEntries = capabilities.RegammaLutEntryCount;
            }
        }

        private static void ReadActiveProfile(DisplayConfigPathSourceInfo source, DisplayInfo display)
        {
            var scopeResult = NativeMethods.ColorProfileGetDisplayUserScope(source.AdapterId, source.Id, out var scope);
            if (scopeResult < 0) scope = WcsProfileManagementScope.CurrentUser;
            display.ProfileScope = scope;

            var result = NativeMethods.ColorProfileGetDisplayDefault(
                scope,
                source.AdapterId,
                source.Id,
                ColorProfileType.Icc,
                ColorProfileSubType.ExtendedDisplayColorMode,
                out var profilePointer);
            if (result < 0 || profilePointer == IntPtr.Zero)
            {
                var alternate = scope == WcsProfileManagementScope.CurrentUser
                    ? WcsProfileManagementScope.SystemWide
                    : WcsProfileManagementScope.CurrentUser;
                result = NativeMethods.ColorProfileGetDisplayDefault(
                    alternate,
                    source.AdapterId,
                    source.Id,
                    ColorProfileType.Icc,
                    ColorProfileSubType.ExtendedDisplayColorMode,
                    out profilePointer);
                if (result >= 0) display.ProfileScope = alternate;
            }

            if (result < 0 || profilePointer == IntPtr.Zero) return;
            try
            {
                display.ActiveProfileName = Marshal.PtrToStringUni(profilePointer);
                if (!string.IsNullOrWhiteSpace(display.ActiveProfileName))
                {
                    display.ActiveProfilePath = Path.IsPathRooted(display.ActiveProfileName)
                        ? display.ActiveProfileName
                        : Path.Combine(GetColorDirectory(), display.ActiveProfileName);
                }
            }
            finally
            {
                NativeMethods.LocalFree(profilePointer);
            }
        }

        private static DisplayConfigDeviceInfoHeader CreateHeader(DisplayConfigDeviceInfoType type, int size, Luid adapter, uint id)
        {
            return new DisplayConfigDeviceInfoHeader
            {
                Type = type,
                Size = checked((uint)size),
                AdapterId = adapter,
                Id = id
            };
        }

        private static void ValidateInteropLayouts()
        {
            RequireSize<DisplayConfigPathInfo>(72);
            RequireSize<DisplayConfigModeInfo>(64);
            RequireSize<DisplayConfigDeviceInfoHeader>(20);
            RequireSize<DisplayConfigAdvancedColorInfo2>(36);
            RequireSize<WcsDeviceMhc2Capabilities>(20);
        }

        private static void RequireSize<T>(int expected)
        {
            var actual = Marshal.SizeOf(typeof(T));
            if (actual != expected)
                throw new InvalidOperationException($"Interop-Layout {typeof(T).Name}: erwartet {expected}, erhalten {actual} Bytes.");
        }
    }

    internal sealed class DxgiOutputData
    {
        public uint BitsPerColor { get; set; }
        public DisplayColorMetadata Metadata { get; set; }
    }

    internal static class DxgiOutputReader
    {
        private static readonly Guid Factory1Guid = new Guid("770AAE78-F26F-4DBA-A829-253C83D1B387");
        private static readonly Guid Output6Guid = new Guid("068346E8-AAEC-4B84-ADD7-137F513F77A1");

        public static Dictionary<string, DxgiOutputData> GetOutputs()
        {
            var result = new Dictionary<string, DxgiOutputData>(StringComparer.OrdinalIgnoreCase);
            var guid = Factory1Guid;
            if (NativeMethods.CreateDXGIFactory1(ref guid, out var factory) < 0 || factory == IntPtr.Zero) return result;
            try
            {
                var enumAdapters = NativeMethods.GetComMethod<NativeMethods.EnumAdapters1Delegate>(factory, 12);
                for (uint adapterIndex = 0; ; adapterIndex++)
                {
                    var hr = enumAdapters(factory, adapterIndex, out var adapter);
                    if (hr == NativeMethods.DxgiErrorNotFound) break;
                    if (hr < 0 || adapter == IntPtr.Zero) break;
                    try
                    {
                        ReadAdapterOutputs(adapter, result);
                    }
                    finally
                    {
                        Marshal.Release(adapter);
                    }
                }
            }
            finally
            {
                Marshal.Release(factory);
            }
            return result;
        }

        private static void ReadAdapterOutputs(IntPtr adapter, Dictionary<string, DxgiOutputData> result)
        {
            var enumOutputs = NativeMethods.GetComMethod<NativeMethods.EnumOutputsDelegate>(adapter, 7);
            for (uint outputIndex = 0; ; outputIndex++)
            {
                var hr = enumOutputs(adapter, outputIndex, out var output);
                if (hr == NativeMethods.DxgiErrorNotFound) break;
                if (hr < 0 || output == IntPtr.Zero) break;
                try
                {
                    var iid = Output6Guid;
                    if (Marshal.QueryInterface(output, ref iid, out var output6) < 0 || output6 == IntPtr.Zero) continue;
                    try
                    {
                        var getDesc = NativeMethods.GetComMethod<NativeMethods.GetDesc1Delegate>(output6, 27);
                        if (getDesc(output6, out var description) < 0 || string.IsNullOrWhiteSpace(description.DeviceName)) continue;
                        var metadata = new DisplayColorMetadata
                        {
                            RedX = description.RedPrimary?[0] ?? 0,
                            RedY = description.RedPrimary?[1] ?? 0,
                            GreenX = description.GreenPrimary?[0] ?? 0,
                            GreenY = description.GreenPrimary?[1] ?? 0,
                            BlueX = description.BluePrimary?[0] ?? 0,
                            BlueY = description.BluePrimary?[1] ?? 0,
                            WhiteX = description.WhitePoint?[0] ?? 0,
                            WhiteY = description.WhitePoint?[1] ?? 0,
                            MinLuminanceNits = description.MinLuminance,
                            MaxLuminanceNits = description.MaxLuminance,
                            MaxFullFrameLuminanceNits = description.MaxFullFrameLuminance
                        };
                        if (!metadata.IsUsable()) metadata = new DisplayColorMetadata();
                        result[description.DeviceName] = new DxgiOutputData
                        {
                            BitsPerColor = description.BitsPerColor,
                            Metadata = metadata
                        };
                    }
                    finally
                    {
                        Marshal.Release(output6);
                    }
                }
                finally
                {
                    Marshal.Release(output);
                }
            }
        }
    }
}
