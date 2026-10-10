using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml;

namespace HDRCalibrate.Setup.Core
{
    public static class SetupPaths
    {
        private static readonly Regex SegmentPattern =
            new Regex(@"\A[A-Za-z0-9][A-Za-z0-9._ -]{0,127}\z", RegexOptions.CultureInvariant);

        /// <summary>
        /// Validates a '/'-separated package path (at most "Native/x.y.z/file") and returns
        /// it with Windows separators. Rooted paths, '.', '..', streams and odd names fail.
        /// </summary>
        public static string ToRelativePath(string packagePath)
        {
            if (string.IsNullOrEmpty(packagePath) || packagePath.Length > 260)
                throw new InvalidDataException("Ungültiger Paketpfad.");
            var segments = packagePath.Split('/');
            if (segments.Length > 3 || segments.Any(segment => !SegmentPattern.IsMatch(segment)
                || segment.EndsWith(".", StringComparison.Ordinal) || segment.EndsWith(" ", StringComparison.Ordinal)))
                throw new InvalidDataException("Ungültiger Paketpfad: " + packagePath);
            if (segments.Length > 1 && !string.Equals(segments[0], ProductInfo.NativeDirectoryName, StringComparison.Ordinal))
                throw new InvalidDataException("Unterordner sind nur unter Native erlaubt: " + packagePath);
            if (segments.Length == 2)
                throw new InvalidDataException("Native-Dateien benötigen einen Versionsordner: " + packagePath);
            return string.Join("\\", segments);
        }

        public static bool IsNative(string relativePath) =>
            relativePath.StartsWith(ProductInfo.NativeDirectoryName + "\\", StringComparison.OrdinalIgnoreCase);

        /// <summary>Combines and proves that the result stays inside <paramref name="root"/>.</summary>
        public static string Combine(string root, string relativePath)
        {
            var fullRoot = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            var full = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
            if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Pfad außerhalb des Zielordners: " + relativePath);
            return full;
        }

        public static bool IsReparsePoint(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }

        public static string Sha256Of(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                return Sha256Of(stream);
        }

        public static string Sha256Of(Stream stream)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
        }
    }

    public sealed class PayloadEntry
    {
        public PayloadEntry(string packagePath, long size, string sha256)
        {
            PackagePath = packagePath;
            RelativePath = SetupPaths.ToRelativePath(packagePath);
            Size = size;
            Sha256 = sha256;
        }

        public string PackagePath { get; }
        public string RelativePath { get; }
        public long Size { get; }
        public string Sha256 { get; }
        public bool IsNative => SetupPaths.IsNative(RelativePath);
        public bool IsNativeHook => IsNative
            && string.Equals(Path.GetFileName(RelativePath), ProductInfo.NativeHookFileName, StringComparison.OrdinalIgnoreCase);
    }

    public sealed class PayloadManifest
    {
        public const string RootName = "SetupPayload";
        private const long MaximumFileSize = 64L * 1024 * 1024;
        private const long MaximumTotalSize = 256L * 1024 * 1024;
        private static readonly Regex VersionPattern = new Regex(@"\A\d{1,5}\.\d{1,5}\.\d{1,5}\z", RegexOptions.CultureInvariant);
        private static readonly Regex HashPattern = new Regex(@"\A[0-9A-F]{64}\z", RegexOptions.CultureInvariant);

        private PayloadManifest(string version, IReadOnlyList<PayloadEntry> entries)
        {
            Version = version;
            Entries = entries;
        }

        public string Version { get; }
        public IReadOnlyList<PayloadEntry> Entries { get; }
        public long TotalSize => Entries.Sum(entry => entry.Size);

        public static PayloadManifest Parse(Stream stream)
        {
            if (stream == null) throw new InvalidDataException("Das Paketmanifest fehlt.");
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 64 * 1024,
                IgnoreComments = true,
                IgnoreWhitespace = true
            };
            var document = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(stream, settings))
                document.Load(reader);
            var root = document.DocumentElement;
            if (root == null || root.Name != RootName || root.Attributes.Count != 1)
                throw new InvalidDataException("Das Paketmanifest hat ein unbekanntes Format.");
            var version = root.GetAttribute("Version");
            if (!VersionPattern.IsMatch(version))
                throw new InvalidDataException("Das Paketmanifest hat keine gültige Version.");

            var entries = new List<PayloadEntry>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (XmlNode node in root.ChildNodes)
            {
                var element = node as XmlElement;
                if (element == null || element.Name != "File" || element.HasChildNodes || element.Attributes.Count != 3)
                    throw new InvalidDataException("Das Paketmanifest enthält einen unbekannten Eintrag.");
                if (!long.TryParse(element.GetAttribute("Size"), NumberStyles.None, CultureInfo.InvariantCulture, out var size)
                    || size <= 0 || size > MaximumFileSize)
                    throw new InvalidDataException("Ungültige Dateigröße im Paketmanifest.");
                var hash = element.GetAttribute("Sha256");
                if (!HashPattern.IsMatch(hash))
                    throw new InvalidDataException("Ungültige Prüfsumme im Paketmanifest.");
                var entry = new PayloadEntry(element.GetAttribute("Path"), size, hash);
                if (entry.IsNative && !entry.RelativePath.StartsWith(
                        ProductInfo.NativeDirectoryName + "\\" + version + "\\", StringComparison.Ordinal))
                    throw new InvalidDataException("Native-Dateien müssen zur Paketversion gehören.");
                if (!paths.Add(entry.RelativePath))
                    throw new InvalidDataException("Doppelter Eintrag im Paketmanifest.");
                entries.Add(entry);
            }
            if (entries.Count == 0 || entries.Count > 64)
                throw new InvalidDataException("Das Paketmanifest hat eine ungültige Anzahl Dateien.");
            if (!paths.Contains(ProductInfo.AppExeName))
                throw new InvalidDataException("Das Paket enthält keine Anwendung.");
            if (entries.Sum(entry => entry.Size) > MaximumTotalSize)
                throw new InvalidDataException("Das Paket ist zu groß.");
            return new PayloadManifest(version, entries);
        }
    }

    /// <summary>The embedded, hash-verified application files.</summary>
    public sealed class PayloadPackage
    {
        public const string ZipResourceName = "HDRCalibrate.Setup.Payload.zip";
        public const string ManifestResourceName = "HDRCalibrate.Setup.PayloadManifest.xml";
        private readonly Func<Stream> _openZip;

        public PayloadPackage(PayloadManifest manifest, Func<Stream> openZip)
        {
            Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
            _openZip = openZip ?? throw new ArgumentNullException(nameof(openZip));
        }

        public PayloadManifest Manifest { get; }

        /// <summary>Returns null when the setup was built without a payload.</summary>
        public static PayloadPackage FromAssembly(Assembly assembly)
        {
            using (var manifest = assembly.GetManifestResourceStream(ManifestResourceName))
            {
                if (manifest == null) return null;
                var parsed = PayloadManifest.Parse(manifest);
                var expected = assembly.GetName().Version.ToString(3);
                if (parsed.Version != expected)
                    throw new InvalidDataException("Paket- und Setupversion unterscheiden sich.");
                return new PayloadPackage(parsed, () => assembly.GetManifestResourceStream(ZipResourceName));
            }
        }

        /// <summary>
        /// Extracts every manifest file into a new directory. The archive must contain
        /// exactly the manifest entries; sizes and SHA-256 values must match.
        /// </summary>
        public void ExtractTo(string directory)
        {
            if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
                throw new IOException("Das Entpackverzeichnis ist nicht leer.");
            Directory.CreateDirectory(directory);
            var byPath = Manifest.Entries.ToDictionary(entry => entry.PackagePath, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            using (var zipStream = _openZip())
            {
                if (zipStream == null) throw new InvalidDataException("Das Paketarchiv fehlt.");
                using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read))
                {
                    foreach (var zipEntry in archive.Entries)
                    {
                        if (zipEntry.FullName.EndsWith("/", StringComparison.Ordinal) && zipEntry.Length == 0) continue;
                        if (!byPath.TryGetValue(zipEntry.FullName, out var entry) || !seen.Add(zipEntry.FullName))
                            throw new InvalidDataException("Unerwartete Datei im Paketarchiv: " + zipEntry.FullName);
                        if (zipEntry.Length != entry.Size)
                            throw new InvalidDataException("Falsche Dateigröße im Paketarchiv: " + entry.PackagePath);
                        var target = SetupPaths.Combine(directory, entry.RelativePath);
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        string hash;
                        using (var source = zipEntry.Open())
                        using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        using (var sha = SHA256.Create())
                        {
                            var buffer = new byte[81920];
                            long written = 0;
                            int read;
                            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                written += read;
                                if (written > entry.Size)
                                    throw new InvalidDataException("Zu große Datei im Paketarchiv: " + entry.PackagePath);
                                sha.TransformBlock(buffer, 0, read, null, 0);
                                output.Write(buffer, 0, read);
                            }
                            sha.TransformFinalBlock(buffer, 0, 0);
                            if (written != entry.Size)
                                throw new InvalidDataException("Unvollständige Datei im Paketarchiv: " + entry.PackagePath);
                            hash = BitConverter.ToString(sha.Hash).Replace("-", string.Empty);
                        }
                        if (!string.Equals(hash, entry.Sha256, StringComparison.Ordinal))
                            throw new InvalidDataException("Prüfsumme stimmt nicht: " + entry.PackagePath);
                    }
                }
            }
            if (seen.Count != byPath.Count)
                throw new InvalidDataException("Das Paketarchiv ist unvollständig.");
        }
    }
}
