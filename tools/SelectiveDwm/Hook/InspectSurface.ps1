[CmdletBinding()]
param(
    [Parameter(Mandatory)][uint32]$DwmPid,
    [Parameter(Mandatory)][uint64]$DwmCreationFileTime,
    [Parameter(Mandatory)][uint64]$SurfaceAddress
)

$ErrorActionPreference = 'Stop'
if (-not ('SelectiveSurfaceReadOnly' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

public static class SelectiveSurfaceReadOnly {
    [StructLayout(LayoutKind.Sequential)] struct Ft { public uint Low, High; }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] struct ModuleEntry {
        public uint Size, ModuleId, Pid, GlobalUsage, ProcessUsage;
        public IntPtr Base; public uint ImageSize; public IntPtr Module;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=256)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=260)] public string Path;
    }
    [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError=true)] static extern uint GetProcessId(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetProcessTimes(IntPtr handle, out Ft creation, out Ft exit, out Ft kernel, out Ft user);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref uint length);
    [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool Module32FirstW(IntPtr snapshot, ref ModuleEntry entry);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool Module32NextW(IntPtr snapshot, ref ModuleEntry entry);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] bytes, UIntPtr length, out UIntPtr read);
    static ulong FtValue(Ft value) { return ((ulong)value.High << 32) | value.Low; }
    static string Hex(ulong value) { return "0x" + value.ToString("X"); }

    sealed class Reader {
        public readonly IntPtr Handle; public int Bytes; public int Reads;
        public Reader(IntPtr handle) { Handle = handle; }
        public byte[] Read(ulong address, int length) {
            if (address < 0x10000 || address > 0x00007FFFFFFFFFFFUL || length <= 0 || length > 256 ||
                address + (ulong)length > 0x0000800000000000UL || Reads >= 32 || Bytes + length > 4096)
                throw new InvalidOperationException("Read outside the bounded user-address contract.");
            byte[] result = new byte[length]; UIntPtr read;
            ++Reads; Bytes += length;
            if (!ReadProcessMemory(Handle, new IntPtr((long)address), result, new UIntPtr((uint)length), out read) ||
                read.ToUInt64() != (ulong)length) throw new Win32Exception(Marshal.GetLastWin32Error());
            return result;
        }
        public ulong U64(ulong address) { return BitConverter.ToUInt64(Read(address,8),0); }
        public uint U32(ulong address) { return BitConverter.ToUInt32(Read(address,4),0); }
    }
    static bool InImage(ulong pointer, ulong imageBase, uint size, uint bytes) {
        return pointer >= imageBase && size >= bytes && pointer-imageBase <= size-bytes;
    }

    public static Dictionary<string,object> Inspect(uint pid, ulong creationTime, ulong surface) {
        var result = new Dictionary<string,object>();
        result["pid"] = pid; result["surface"] = Hex(surface);
        result["target_writes"] = 0; result["remote_calls"] = 0;
        // Explicitly no VM_OPERATION/VM_WRITE/CREATE_THREAD/DUP_HANDLE and no
        // privilege adjustment. No other process is opened by this diagnostic.
        IntPtr process = OpenProcess(0x1010, false, pid);
        if (process == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        Reader reader = new Reader(process);
        try {
            if (GetProcessId(process) != pid) throw new InvalidOperationException("PID mismatch.");
            Ft creation, exited, kernel, user;
            if (!GetProcessTimes(process, out creation, out exited, out kernel, out user) ||
                FtValue(creation) != creationTime) throw new InvalidOperationException("DWM process identity changed.");
            var image = new StringBuilder(1024); uint length=1024;
            if (!QueryFullProcessImageName(process, 0, image, ref length) ||
                !String.Equals(image.ToString(), System.IO.Path.Combine(Environment.SystemDirectory,"dwm.exe"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Not the exact system DWM executable.");
            result["creation_filetime"] = creationTime;
            IntPtr snapshot = CreateToolhelp32Snapshot(0x18,pid);
            if (snapshot == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            ulong coreBase=0; uint coreSize=0;
            try {
                ModuleEntry entry=new ModuleEntry(); entry.Size=(uint)Marshal.SizeOf(typeof(ModuleEntry));
                bool next=Module32FirstW(snapshot,ref entry);
                for(int i=0;next && i<512;++i) {
                    if(String.Equals(entry.Path,System.IO.Path.Combine(Environment.SystemDirectory,"dwmcore.dll"),StringComparison.OrdinalIgnoreCase)) {
                        coreBase=(ulong)entry.Base.ToInt64(); coreSize=entry.ImageSize; break;
                    }
                    next=Module32NextW(snapshot,ref entry);
                }
            } finally { CloseHandle(snapshot); }
            if(coreBase==0) throw new InvalidOperationException("Exact system dwmcore module unavailable.");
            result["dwmcore_base"]=Hex(coreBase); result["dwmcore_size"]=coreSize;
            ulong surfaceVtable=reader.U64(surface);
            result["surface_vtable_rva"]=Hex(surfaceVtable-coreBase);
            if(surfaceVtable!=coreBase+0x2F8298) { result["stop"]="SurfaceVtableMismatch"; return result; }
            ulong bind=reader.U64(surface+0x88), realization=reader.U64(surface+0xD0);
            result["current_bind_id"]=bind; result["realization"]=Hex(realization);
            if(realization==0) { result["stop"]="NoCurrentRealization"; return result; }
            ulong sourceTable=reader.U64(realization+8);
            result["source_vtable_rva"]=Hex(sourceTable-coreBase);
            if(!InImage(sourceTable,coreBase,coreSize,0x20)) { result["stop"]="SourceVtableOutsideDwmcore"; return result; }
            ulong sourceGetter=reader.U64(sourceTable+0x18);
            result["source_getter_rva"]=Hex(sourceGetter-coreBase);
            string sourceBytes="";
            if(InImage(sourceGetter,coreBase,coreSize,32)) {
                sourceBytes=BitConverter.ToString(reader.Read(sourceGetter,32)).Replace("-","");
                result["source_getter_bytes"]=sourceBytes;
            }
            if(sourceGetter!=coreBase+0x2D6DC0) { result["stop"]="SourceGetterMismatch"; return result; }
            if(!sourceBytes.StartsWith("4881E900010000E994A9DBFF",StringComparison.Ordinal)) {
                result["stop"]="SourceGetterBytesMismatch"; return result;
            }
            ulong backing=reader.U64(realization+0x30);
            result["backing"]=Hex(backing);
            if(backing==0) { result["stop"]="NoBackingSurface"; return result; }
            ulong formatTable=reader.U64(backing+0x50);
            result["format_vtable_rva"]=Hex(formatTable-coreBase);
            if(!InImage(formatTable,coreBase,coreSize,0x20)) { result["stop"]="FormatVtableOutsideDwmcore"; return result; }
            ulong formatGetter=reader.U64(formatTable+0x18);
            result["format_getter_rva"]=Hex(formatGetter-coreBase);
            string formatBytes="";
            if(InImage(formatGetter,coreBase,coreSize,32)) {
                formatBytes=BitConverter.ToString(reader.Read(formatGetter,32)).Replace("-","");
                result["format_getter_bytes"]=formatBytes;
            }
            uint formatOffset=0, alphaOffset=0, colorOffset=0; string expectedBytes="";
            if(formatTable==coreBase+0x30A728 && formatGetter==coreBase+0x2C7C10) {
                result["format_leaf"]="CD3DSurface";
                formatOffset=0x74; alphaOffset=0x90; colorOffset=0x94;
                expectedBytes="8B412489028B41408942048B4144894208488BC2C3";
            } else if(formatTable==coreBase+0x2F97E8 && formatGetter==coreBase+0x1BF4A0) {
                result["format_leaf"]="CD2DBitmap";
                formatOffset=0xA0; alphaOffset=0xC4; colorOffset=0xE0;
                expectedBytes="8B415089028B41748942048B8190000000894208488BC2C3";
            } else { result["stop"]="FormatLeafMismatch"; return result; }
            if(!formatBytes.StartsWith(expectedBytes,StringComparison.Ordinal)) {
                result["stop"]="FormatLeafBytesMismatch"; return result;
            }
            result["pixel_format"]=reader.U32(backing+formatOffset);
            result["alpha_mode"]=reader.U32(backing+alphaOffset);
            result["color_space"]=reader.U32(backing+colorOffset);
            // RPM does not hold the compositor's lock. Reject a changed owner
            // chain instead of treating reads from different moments as one
            // coherent source descriptor.
            if(reader.U64(surface)!=surfaceVtable || reader.U64(surface+0x88)!=bind ||
                reader.U64(surface+0xD0)!=realization || reader.U64(realization+0x30)!=backing ||
                reader.U64(backing+0x50)!=formatTable) {
                result.Remove("pixel_format"); result.Remove("alpha_mode"); result.Remove("color_space");
                result["stop"]="SourceChangedDuringRead"; return result;
            }
            result["identity_rechecked"]=true;
            result["stop"]="VerifiedLeafRead";
            return result;
        } finally {
            result["bounded_reads"]=reader.Reads; result["bounded_bytes"]=reader.Bytes;
            CloseHandle(process);
        }
    }
}
'@
}
$expectedHash = '825726F26BD6B5A02B49F6145963B54D537D3D1239055DDA28D1FB0B3E864EF3'
$coreFile = Join-Path ([Environment]::SystemDirectory) 'dwmcore.dll'
if ((Get-FileHash -LiteralPath $coreFile -Algorithm SHA256).Hash -ne $expectedHash) {
    throw 'Unsupported dwmcore binary; no process reads attempted.'
}
[SelectiveSurfaceReadOnly]::Inspect($DwmPid, $DwmCreationFileTime, $SurfaceAddress) | ConvertTo-Json -Depth 4 -Compress
