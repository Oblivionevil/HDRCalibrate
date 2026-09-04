using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace HDRCalibrate.Dwm
{
    internal enum DwmShaderProfile
    {
        Level10_0,
        Level9_3,
        Level9_1
    }

    internal enum DwmShaderKind
    {
        Alpha,
        Opaque
    }

    internal sealed class DwmTargetShader
    {
        public string ChecksumHex { get; set; }
        public string FunctionName { get; set; }
        public DwmShaderProfile Profile { get; set; }
        public DwmShaderKind Kind { get; set; }
        public bool IsPatchTarget { get; set; }
        public string ParentChecksumHex { get; set; }
        public int ParentFunctionCount { get; set; }
        public int FunctionIndex { get; set; }
        public int SlotSize { get; set; }
        public string[] OrderedChunks { get; set; }
    }

    internal sealed class ValidatedDwmShader
    {
        public DwmTargetShader Specification { get; set; }
        public DxbcBlob Blob { get; set; }
        public DxbcBlob Parent { get; set; }
    }

    internal sealed class DwmShaderManifestValidation
    {
        public IReadOnlyList<DxbcBlob> AllBlobs { get; set; }
        public IReadOnlyList<ValidatedDwmShader> Shaders { get; set; }
        public IReadOnlyList<DxbcBlob> Parents { get; set; }
    }

    public static class DwmCoreInspector
    {
        public const string SupportedDwmCoreVersion = "10.0.26100.8875 (WinBuild.160101.0800)";
        public const string SupportedDwmCoreSha256 = "825726F26BD6B5A02B49F6145963B54D537D3D1239055DDA28D1FB0B3E864EF3";

        private static readonly string[] NativeChunks = { "RDEF", "LFS0", "SHDR", "STAT" };
        private static readonly string[] DownlevelChunks = { "Aon9", "SHDR", "STAT", "RDEF", "LFS0" };

        internal static readonly DwmTargetShader[] AllSdrShaders =
        {
            NewShader("4CDE30EDFF9BEE225DA9465FDF59CD84", D3DCompiler.AlphaFunctionName,
                DwmShaderProfile.Level10_0, DwmShaderKind.Alpha, false,
                "CA9E43FD4AE1299B99BAAC5751EDD17F", 115, 10, 1024, NativeChunks),
            NewShader("7042A5DE5C816B2D89D14AD3CCD76569", D3DCompiler.OpaqueFunctionName,
                DwmShaderProfile.Level10_0, DwmShaderKind.Opaque, false,
                "CA9E43FD4AE1299B99BAAC5751EDD17F", 115, 11, 896, NativeChunks),
            NewShader("96E6D1589255ECCD1DD7D4DBEC54D285", D3DCompiler.AlphaFunctionName,
                DwmShaderProfile.Level9_3, DwmShaderKind.Alpha, true,
                "C0E76587B379EC9A5CFCE9CAAC0ACCE1", 115, 10, 1412, DownlevelChunks),
            NewShader("2126B037C1A2FBDDE355B6E6DD9CAF3C", D3DCompiler.OpaqueFunctionName,
                DwmShaderProfile.Level9_3, DwmShaderKind.Opaque, true,
                "C0E76587B379EC9A5CFCE9CAAC0ACCE1", 115, 11, 1220, DownlevelChunks),
            NewShader("2C8926FFE229F05D967C72668DC3ADDB", D3DCompiler.AlphaFunctionName,
                DwmShaderProfile.Level9_1, DwmShaderKind.Alpha, true,
                "6BA40A153D1849B95646813137B7E11C", 83, 10, 1424, DownlevelChunks),
            NewShader("F693BFBBAF24B3D9366354BE8898A7F5", D3DCompiler.OpaqueFunctionName,
                DwmShaderProfile.Level9_1, DwmShaderKind.Opaque, true,
                "6BA40A153D1849B95646813137B7E11C", 83, 11, 1220, DownlevelChunks)
        };

        internal static readonly DwmTargetShader[] Targets =
            AllSdrShaders.Where(shader => shader.IsPatchTarget).ToArray();

        public static string GetSystemDwmCorePath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "dwmcore.dll");
        }

        public static DwmCompatibilityInfo InspectSystem()
        {
            return InspectFile(GetSystemDwmCorePath());
        }

        public static DwmCompatibilityInfo InspectFile(string path)
        {
            var result = new DwmCompatibilityInfo { DwmCorePath = path };
            try
            {
                var fullPath = Path.GetFullPath(path);
                if (!File.Exists(fullPath))
                    throw new FileNotFoundException("dwmcore.dll wurde nicht gefunden.", fullPath);

                result.DwmCorePath = fullPath;
                result.DwmCoreVersion = FileVersionInfo.GetVersionInfo(fullPath).FileVersion;
                var image = File.ReadAllBytes(fullPath);
                result.DwmCoreSha256 = ComputeSha256(image);
                if (!string.Equals(result.DwmCoreVersion, SupportedDwmCoreVersion, StringComparison.Ordinal)
                    || !string.Equals(result.DwmCoreSha256, SupportedDwmCoreSha256, StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException(
                        "Diese dwmcore.dll-Version ist nicht freigegeben. Erwartet: " + SupportedDwmCoreVersion +
                        " / SHA-256 " + SupportedDwmCoreSha256 + "; gefunden: " + result.DwmCoreVersion +
                        " / " + result.DwmCoreSha256 + ".");

                var validation = ValidateShaderManifest(image);
                result.TargetShaderCount = Targets.Length;
                result.ValidatedSdrShaderCount = validation.Shaders.Count;
                result.ValidatedParentLibraryCount = validation.Parents.Count;
                result.IsCompatible = validation.Shaders.Count == AllSdrShaders.Length && validation.Parents.Count == 3;
                result.Reason = result.IsCompatible
                    ? "Alle sechs DWM-SDR→scRGB-Exporte in drei Profilbibliotheken wurden per Hash, Struktur und D3D-Reflection validiert; nur die vier 9.1/9.3-Exporte sind Patchziele."
                    : "Das erwartete DWM-SDR-Shadermanifest ist unvollständig.";
            }
            catch (Exception ex)
            {
                result.IsCompatible = false;
                result.Reason = ex.Message;
            }
            return result;
        }

        internal static DwmShaderManifestValidation ValidateShaderManifest(byte[] image)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));
            var blobs = DxbcContainer.FindAll(image);
            var manifestChecksums = new HashSet<string>(
                AllSdrShaders.Select(shader => shader.ChecksumHex), StringComparer.OrdinalIgnoreCase);
            var candidateFunctions = blobs
                .Where(blob => !blob.Chunks.Any(chunk => string.Equals(chunk.FourCc, "LIBF", StringComparison.Ordinal))
                    && blob.GetChunk("LFS0") != null
                    && (DxbcContainer.ContainsAscii(image, blob, D3DCompiler.AlphaFunctionName)
                        || DxbcContainer.ContainsAscii(image, blob, D3DCompiler.OpaqueFunctionName)))
                .ToList();
            if (candidateFunctions.Count != AllSdrShaders.Length
                || candidateFunctions.Any(blob => !manifestChecksums.Contains(DxbcContainer.GetStoredChecksumHex(image, blob))))
                throw new InvalidDataException(
                    "dwmcore.dll enthält " + candidateFunctions.Count +
                    " statt exakt sechs freigegebener SDR→scRGB-Funktionsvarianten.");

            var validated = new List<ValidatedDwmShader>(AllSdrShaders.Length);
            foreach (var specification in AllSdrShaders)
            {
                var matches = blobs.Where(blob => string.Equals(
                    DxbcContainer.GetStoredChecksumHex(image, blob), specification.ChecksumHex,
                    StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count != 1)
                    throw new InvalidDataException(
                        "Shader " + specification.FunctionName + " mit Hash " + specification.ChecksumHex +
                        " wurde " + matches.Count + "-mal statt genau einmal gefunden.");

                var match = matches[0];
                if (!DxbcContainer.VerifyChecksum(image, match))
                    throw new InvalidDataException("Die DXBC-Prüfsumme eines SDR-Manifesteintrags ist ungültig.");
                if (match.Size != specification.SlotSize)
                    throw new InvalidDataException("Ein SDR-Manifesteintrag besitzt nicht die freigegebene LIBF-Slotgröße.");
                if (!DxbcContainer.ContainsAscii(image, match, specification.FunctionName))
                    throw new InvalidDataException("Ein SDR-Manifesteintrag trägt nicht den erwarteten Exportnamen.");
                if (!match.Chunks.Select(chunk => chunk.FourCc).SequenceEqual(specification.OrderedChunks))
                    throw new InvalidDataException("Ein SDR-Manifesteintrag besitzt nicht die freigegebene Chunkfolge.");

                var parent = FindParentLibrary(blobs, match);
                if (parent == null)
                    throw new InvalidDataException("Der exakte LIBF-Elterncontainer eines SDR-Manifesteintrags fehlt.");
                if (!DxbcContainer.VerifyChecksum(image, parent))
                    throw new InvalidDataException("Die Prüfsumme einer DWM-Shaderbibliothek ist ungültig.");
                if (!string.Equals(DxbcContainer.GetStoredChecksumHex(image, parent), specification.ParentChecksumHex,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Ein SDR-Export liegt nicht in der freigegebenen Profilbibliothek.");

                var libfChunks = parent.Chunks
                    .Where(chunk => string.Equals(chunk.FourCc, "LIBF", StringComparison.Ordinal)).ToList();
                if (libfChunks.Count != specification.ParentFunctionCount)
                    throw new InvalidDataException("Eine DWM-Profilbibliothek besitzt eine unerwartete Funktionsanzahl.");
                var functionIndex = libfChunks.FindIndex(chunk =>
                    chunk.DataOffset == match.Offset && chunk.DataSize == match.Size);
                if (functionIndex != specification.FunctionIndex)
                    throw new InvalidDataException("Ein DWM-SDR-Export liegt am falschen Bibliotheksindex.");

                validated.Add(new ValidatedDwmShader
                {
                    Specification = specification,
                    Blob = match,
                    Parent = parent
                });
            }

            var parents = validated.Select(item => item.Parent)
                .GroupBy(parent => parent.Offset).Select(group => group.First()).ToList();
            if (parents.Count != 3)
                throw new InvalidDataException("Die sechs SDR-Exporte liegen nicht in exakt drei Profilbibliotheken.");

            foreach (var parent in parents)
            {
                var entries = validated.Where(item => item.Parent.Offset == parent.Offset).ToList();
                if (entries.Count != 2 || entries.Select(item => item.Specification.Kind).Distinct().Count() != 2)
                    throw new InvalidDataException("Eine DWM-Profilbibliothek enthält nicht exakt das erwartete Exportpaar.");
                ValidateParentReflection(image, parent, entries.Select(item => item.Specification));
            }

            return new DwmShaderManifestValidation
            {
                AllBlobs = blobs,
                Shaders = validated,
                Parents = parents
            };
        }

        internal static void ValidateParentReflection(
            byte[] image,
            DxbcBlob parent,
            IEnumerable<DwmTargetShader> specifications)
        {
            var entries = specifications.ToList();
            var library = D3DLibraryReflection.Reflect(DxbcContainer.Slice(image, parent));
            var expectedFunctionCount = entries.Select(entry => entry.ParentFunctionCount).Distinct().Single();
            if (library.Functions.Count != expectedFunctionCount || library.Flags != 0)
                throw new InvalidDataException("D3D-Reflection meldet unerwartete Bibliotheksmetadaten.");
            foreach (var entry in entries)
            {
                if (entry.FunctionIndex < 0 || entry.FunctionIndex >= library.Functions.Count)
                    throw new InvalidDataException("Ein SDR-Funktionsindex liegt außerhalb der reflektierten Bibliothek.");
                ValidateFunctionReflection(library.Functions[entry.FunctionIndex], entry);
            }
        }

        internal static DxbcBlob FindParentLibrary(IReadOnlyList<DxbcBlob> blobs, DxbcBlob child)
        {
            return blobs
                .Where(candidate => candidate.Offset < child.Offset && candidate.EndOffset >= child.EndOffset)
                .OrderBy(candidate => candidate.Size)
                .FirstOrDefault(candidate => candidate.Chunks.Any(chunk =>
                    string.Equals(chunk.FourCc, "LIBF", StringComparison.Ordinal)
                    && chunk.DataOffset == child.Offset && chunk.DataSize == child.Size));
        }

        internal static IReadOnlyList<DxbcBlob> FindDirectAncestors(
            IReadOnlyList<DxbcBlob> blobs,
            IEnumerable<DxbcBlob> children)
        {
            var frontier = new Queue<DxbcBlob>(children);
            var result = new Dictionary<int, DxbcBlob>();
            while (frontier.Count > 0)
            {
                var child = frontier.Dequeue();
                foreach (var parent in blobs.Where(candidate => candidate.Offset < child.Offset
                    && candidate.Chunks.Any(chunk => chunk.DataOffset == child.Offset && chunk.DataSize == child.Size)))
                {
                    if (result.ContainsKey(parent.Offset)) continue;
                    result[parent.Offset] = parent;
                    frontier.Enqueue(parent);
                }
            }
            return result.Values.OrderBy(blob => blob.Size).ToList();
        }

        private static void ValidateFunctionReflection(D3DFunctionInfo function, DwmTargetShader specification)
        {
            var expectedFeatureLevel = specification.Profile == DwmShaderProfile.Level10_0
                ? 0xA000u : specification.Profile == DwmShaderProfile.Level9_3 ? 0x9300u : 0x9100u;
            var expected10Level9 = specification.Profile != DwmShaderProfile.Level10_0;
            if (!string.Equals(function.Name, specification.FunctionName, StringComparison.Ordinal)
                || function.Version != 0xFFF00040u
                || function.Flags != 0x8100u
                || function.MinFeatureLevel != expectedFeatureLevel
                || function.RequiredFeatureFlags != 0
                || !function.HasReturn
                || function.Has10Level9VertexShader
                || function.Has10Level9PixelShader != expected10Level9
                || function.Parameters == null
                || function.Parameters.Count != 1)
                throw new InvalidDataException("Die reflektierte Profil- oder Funktions-ABI eines DWM-SDR-Exports weicht ab.");

            ValidateParameter(function.Parameters[0], 1, true);
            ValidateParameter(function.ReturnParameter, 2, false);
        }

        private static void ValidateParameter(D3DParameterInfo parameter, int flags, bool input)
        {
            if (parameter == null || parameter.Type != 3 || parameter.Class != 1
                || parameter.Rows != 1 || parameter.Columns != 4
                || parameter.Flags != flags || parameter.InterpolationMode != 0
                || (input && (parameter.FirstInRegister != 0 || parameter.FirstInComponent != 0))
                || (!input && (parameter.FirstOutRegister != 0 || parameter.FirstOutComponent != 0)))
                throw new InvalidDataException("Ein DWM-SDR-Export besitzt nicht exakt die erwartete float4-Parameter-ABI.");
        }

        private static DwmTargetShader NewShader(
            string checksum,
            string functionName,
            DwmShaderProfile profile,
            DwmShaderKind kind,
            bool isPatchTarget,
            string parentChecksum,
            int parentFunctionCount,
            int functionIndex,
            int slotSize,
            string[] orderedChunks)
        {
            return new DwmTargetShader
            {
                ChecksumHex = checksum,
                FunctionName = functionName,
                Profile = profile,
                Kind = kind,
                IsPatchTarget = isPatchTarget,
                ParentChecksumHex = parentChecksum,
                ParentFunctionCount = parentFunctionCount,
                FunctionIndex = functionIndex,
                SlotSize = slotSize,
                OrderedChunks = orderedChunks
            };
        }

        private static string ComputeSha256(byte[] image)
        {
            using (var sha = SHA256.Create())
                return DxbcContainer.ToHex(sha.ComputeHash(image));
        }
    }
}
