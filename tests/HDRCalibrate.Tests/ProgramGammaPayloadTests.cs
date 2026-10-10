using HDRCalibrate.Services;
using System;
using System.IO;

namespace HDRCalibrate.Tests
{
    internal static class ProgramGammaPayloadTests
    {
        public static void ManifestAndLockedNativeFiles()
        {
            using (var files = ProgramGammaNativeFiles.OpenVerified())
            {
                var expected = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Native",
                    typeof(ProgramGammaNativeFiles).Assembly.GetName().Version.ToString(3));
                Require(string.Equals(Path.GetDirectoryName(files.ControllerPath), expected, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Path.GetDirectoryName(files.HookPath), expected, StringComparison.OrdinalIgnoreCase),
                    "Native payload was resolved outside the versioned application bundle.");
                foreach (var path in new[] { files.ControllerPath, files.HookPath })
                {
                    using (var stream = File.OpenRead(path))
                    using (var reader = new BinaryReader(stream))
                    {
                        Require(reader.ReadUInt16() == 0x5a4d, "Missing native DOS header.");
                        stream.Position = 0x3c;
                        stream.Position = reader.ReadInt32();
                        Require(reader.ReadUInt32() == 0x4550 && reader.ReadUInt16() == 0x8664, "Native payload is not x64 PE.");
                    }
                    var writeRefused = false;
                    try { using (File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) { } }
                    catch (IOException) { writeRefused = true; }
                    Require(writeRefused, "Verified native bytes are not locked against replacement/write during a session.");
                }
                Require(File.Exists(Path.Combine(expected, "MinHook-LICENSE.txt")), "MinHook license is missing from the native bundle.");
            }
        }

        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
