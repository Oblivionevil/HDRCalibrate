using System;
using System.Runtime.InteropServices;
using System.Text;

namespace HDRCalibrate.Dwm
{
    /// <summary>
    /// Read-only DXBC validation helpers. HDR Calibrate deliberately contains no shader
    /// compiler or whole-function replacement path; live changes are limited to the
    /// allow-listed constants in the original Windows shader containers.
    /// </summary>
    public static class D3DCompiler
    {
        public const string AlphaFunctionName = "RGB_FULL_G22_NONE_P709_To_scRGB";
        public const string OpaqueFunctionName = "RGB_FULL_G22_NONE_P709_IA_To_scRGB";

        [ComImport]
        [Guid("8BA5FB08-5195-40E2-AC58-0D989C3A0102")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ID3DBlob
        {
            [PreserveSig]
            IntPtr GetBufferPointer();

            [PreserveSig]
            UIntPtr GetBufferSize();
        }

        [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        private static extern int D3DDisassemble(
            byte[] sourceData,
            IntPtr sourceDataSize,
            uint flags,
            string comments,
            out IntPtr disassembly);

        public static void ValidateDxbc(byte[] bytes, string label)
        {
            if (bytes == null || bytes.Length < 32) throw new ArgumentException("DXBC-Daten fehlen.", nameof(bytes));
            Disassemble(bytes, label);
        }

        public static string Disassemble(byte[] bytes, string label = "DXBC-Shader")
        {
            if (bytes == null || bytes.Length < 32) throw new ArgumentException("DXBC-Daten fehlen.", nameof(bytes));
            IntPtr disassembly = IntPtr.Zero;
            try
            {
                var hr = D3DDisassemble(bytes, new IntPtr(bytes.Length), 0, null, out disassembly);
                if (hr < 0)
                    throw new InvalidOperationException(label + " wurde von D3DCompiler abgelehnt (HRESULT 0x" + hr.ToString("X8") + ").");
                return ReadBlobText(disassembly);
            }
            finally
            {
                ReleaseBlob(disassembly);
            }
        }

        private static byte[] ReadBlobBytes(IntPtr pointer)
        {
            if (pointer == IntPtr.Zero) return null;
            var blob = (ID3DBlob)Marshal.GetObjectForIUnknown(pointer);
            try
            {
                var length64 = blob.GetBufferSize().ToUInt64();
                if (length64 > int.MaxValue) throw new InvalidOperationException("D3DCompiler lieferte einen zu großen Blob.");
                var result = new byte[(int)length64];
                if (result.Length > 0) Marshal.Copy(blob.GetBufferPointer(), result, 0, result.Length);
                return result;
            }
            finally
            {
                Marshal.FinalReleaseComObject(blob);
            }
        }

        private static string ReadBlobText(IntPtr pointer)
        {
            var bytes = ReadBlobBytes(pointer);
            return bytes == null ? string.Empty : Encoding.ASCII.GetString(bytes).TrimEnd('\0', '\r', '\n');
        }

        private static void ReleaseBlob(IntPtr pointer)
        {
            if (pointer != IntPtr.Zero) Marshal.Release(pointer);
        }
    }
}
