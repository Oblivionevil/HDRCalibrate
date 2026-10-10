using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace HDRCalibrate.Setup.Core
{
    /// <summary>
    /// Files written by any setup run into the installation directory (native
    /// version folders excluded; they follow their own rules). Uninstall removes
    /// only these files, so foreign files in the folder are never touched.
    /// </summary>
    public sealed class InstallationRecord
    {
        private const string Header = "HDRCalibrate.Setup.Record/1";

        public InstallationRecord(bool createdDirectory, IEnumerable<string> files)
        {
            CreatedDirectory = createdDirectory;
            Files = new SortedSet<string>(files ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        }

        public bool CreatedDirectory { get; }
        public SortedSet<string> Files { get; }

        public static InstallationRecord Read(string installDirectory, ISetupLog log)
        {
            var path = Path.Combine(installDirectory, ProductInfo.RecordFileName);
            if (!File.Exists(path)) return null;
            string[] lines;
            try { lines = File.ReadAllLines(path, Encoding.UTF8); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                log?.Write("Installationsprotokoll nicht lesbar: " + ex.Message);
                return null;
            }
            if (lines.Length == 0 || lines[0] != Header) return null;
            var created = false;
            var files = new List<string>();
            foreach (var line in lines.Skip(1))
            {
                if (line == "created-directory=1") created = true;
                else if (line.StartsWith("file=", StringComparison.Ordinal))
                {
                    try
                    {
                        var relative = SetupPaths.ToRelativePath(line.Substring(5));
                        if (!SetupPaths.IsNative(relative)) files.Add(relative);
                    }
                    catch (InvalidDataException) { log?.Write("Ungültiger Eintrag im Installationsprotokoll ignoriert."); }
                }
            }
            return new InstallationRecord(created, files);
        }

        public void WriteTo(string path)
        {
            var builder = new StringBuilder();
            builder.Append(Header).Append("\r\n");
            if (CreatedDirectory) builder.Append("created-directory=1\r\n");
            foreach (var file in Files) builder.Append("file=").Append(file.Replace('\\', '/')).Append("\r\n");
            File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
        }
    }
}
