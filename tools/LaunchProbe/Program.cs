using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml.Linq;

// Hidden, short-lived test executable. It never starts a browser or modifies
// DWM, and writes only its own adjacent result file inside the test fixture.
internal static class Program
{
    private static int Main(string[] args)
    {
        using (var identity = WindowsIdentity.GetCurrent())
        using (var process = Process.GetCurrentProcess())
        {
            if (!GetTokenInformation(identity.Token, 20, out var elevated, sizeof(int), out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var report = new XDocument(new XElement("LaunchProbe",
                new XElement("Elevated", elevated != 0),
                new XElement("IntegrityRid", GetIntegrityRid(identity.Token)),
                new XElement("UserSid", identity.User.Value),
                new XElement("SessionId", process.SessionId),
                new XElement("ProcessId", process.Id),
                new XElement("WorkingDirectory", Environment.CurrentDirectory),
                new XElement("Arguments", args.Select(value => new XElement("Argument", value)))));
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launch-probe-result.xml");
            report.Save(path + ".tmp");
            File.Move(path + ".tmp", path);
        }
        return 0;
    }

    private static int GetIntegrityRid(IntPtr token)
    {
        GetTokenInformationBuffer(token, 25, IntPtr.Zero, 0, out var length);
        if (length <= 0 || length > 65536) throw new InvalidOperationException("Invalid token label length.");
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetTokenInformationBuffer(token, 25, buffer, length, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var sid = Marshal.ReadIntPtr(buffer);
            var count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
            return Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int information, int length, out int returnedLength);
    [DllImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformationBuffer(IntPtr token, int infoClass, IntPtr information, int length, out int returnedLength);
    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
}
