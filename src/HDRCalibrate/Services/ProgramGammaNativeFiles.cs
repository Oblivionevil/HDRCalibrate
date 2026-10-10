using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml;

namespace HDRCalibrate.Services
{
    public sealed class ProgramGammaNativeFiles : IDisposable
    {
        private readonly List<FileStream> _locks = new List<FileStream>();
        public string ControllerPath { get; private set; }
        public string HookPath { get; private set; }

        public static ProgramGammaNativeFiles OpenVerified()
        {
            var files = new ProgramGammaNativeFiles();
            try
            {
                var assembly = typeof(ProgramGammaNativeFiles).Assembly;
                var version = assembly.GetName().Version.ToString(3);
                using (var resource = assembly.GetManifestResourceStream("HDRCalibrate.ProgramGammaNativeManifest.xml"))
                {
                    if (resource == null) throw new InvalidDataException("Der geprüfte native Gamma-Build fehlt. Bitte das vollständige Paket verwenden.");
                    var xml = new XmlDocument { XmlResolver = null };
                    using (var reader = XmlReader.Create(resource, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4096 }))
                        xml.Load(reader);
                    if (xml.DocumentElement?.Name != "ProgramGammaNative" || xml.DocumentElement.GetAttribute("Version") != version)
                        throw new InvalidDataException("Das native Gamma-Manifest passt nicht zur Anwendung.");
                    var directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Native", version);
                    files.ControllerPath = Path.Combine(directory, "HDRCalibrate.SelectiveController.exe");
                    files.HookPath = Path.Combine(directory, "HDRCalibrate.SelectiveDwmHook.dll");
                    files.Check(files.ControllerPath, xml.SelectSingleNode("/ProgramGammaNative/ControllerSha256")?.InnerText);
                    files.Check(files.HookPath, xml.SelectSingleNode("/ProgramGammaNative/HookSha256")?.InnerText);
                }
                return files;
            }
            catch { files.Dispose(); throw; }
        }

        private void Check(string path, string expected)
        {
            if (expected == null || !Regex.IsMatch(expected, @"\A[0-9A-Fa-f]{64}\z")) throw new InvalidDataException("Ungültiger nativer Hashnachweis.");
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            _locks.Add(file);
            using (var hash = SHA256.Create())
                if (!string.Equals(BitConverter.ToString(hash.ComputeHash(file)).Replace("-", ""), expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Der native Gamma-Helfer wurde verändert. Aktivierung gesperrt.");
        }

        public void Dispose()
        {
            foreach (var file in _locks) file.Dispose();
            _locks.Clear();
        }
    }
}
