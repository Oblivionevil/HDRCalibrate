using HDRCalibrate.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HDRCalibrate.Dwm
{
    public static class DwmShaderRewriter
    {
        private const int PageSize = 4096;
        private const float OriginalExponent = 2.4f;
        private const float OriginalBreakpoint = 0.04045f;
        private const float OriginalOffset = 0.055f;
        private const float OriginalScale = 0.94786733f;

        private sealed class ConstantPatch
        {
            public string Name { get; set; }
            public float From { get; set; }
            public float To { get; set; }
        }

        public static byte[] Rewrite(byte[] originalImage, CalibrationSettings settings, out DwmRewriteReport report)
        {
            if (originalImage == null) throw new ArgumentNullException(nameof(originalImage));
            ValidateSupportedSettings(settings);

            var manifest = DwmCoreInspector.ValidateShaderManifest(originalImage);
            var modified = (byte[])originalImage.Clone();
            var targetEntries = manifest.Shaders.Where(item => item.Specification.IsPatchTarget).ToList();
            var parentEntries = targetEntries.GroupBy(item => item.Parent.Offset).ToList();
            var allowedChanges = new HashSet<int>();
            var constantPatches = BuildConstantPatches(settings);
            var patchedConstants = 0;

            foreach (var entry in targetEntries)
            {
                AddRange(allowedChanges, entry.Blob.Offset + 4, 16);
                var shaderChunk = entry.Blob.GetChunk("SHDR");
                if (shaderChunk == null)
                    throw new InvalidDataException("Ein freigegebener SDR-Zielshader besitzt keinen SHDR-Code.");

                foreach (var constantPatch in constantPatches)
                {
                    var matches = FindFloatTriple(
                        originalImage,
                        shaderChunk.DataOffset,
                        shaderChunk.DataSize,
                        constantPatch.From);
                    if (matches.Count != 1)
                        throw new InvalidDataException(
                            "Die Konstante " + constantPatch.Name + " wurde im freigegebenen SHDR-Code " +
                            matches.Count + "-mal statt genau einmal gefunden.");

                    WriteFloatTriple(modified, matches[0], constantPatch.To);
                    if (!FloatTripleEquals(modified, matches[0], constantPatch.To))
                        throw new InvalidDataException("Eine SHDR-Konstante konnte nicht bytegenau ersetzt werden.");
                    AddRange(allowedChanges, matches[0], 12);
                    patchedConstants++;
                }
                DxbcContainer.UpdateChecksum(modified, entry.Blob);
            }

            if (targetEntries.Count != 6 || targetEntries.Count != DwmCoreInspector.Targets.Length || patchedConstants != 24)
                throw new InvalidDataException("Der SDR-Konstantenpatch hat nicht exakt sechs Shader und 24 Konstantenvektoren vorbereitet.");

            var changedParents = new List<DxbcBlob>();
            foreach (var parentGroup in parentEntries)
            {
                var parent = parentGroup.First().Parent;
                AddRange(allowedChanges, parent.Offset + 4, 16);
                DxbcContainer.UpdateChecksum(modified, parent);
                changedParents.Add(parent);
            }

            var ancestors = DwmCoreInspector.FindDirectAncestors(manifest.AllBlobs, changedParents)
                .Where(ancestor => changedParents.All(parent => ancestor.Offset != parent.Offset)).ToList();
            foreach (var ancestor in ancestors)
            {
                if (!DxbcContainer.VerifyChecksum(originalImage, ancestor))
                    throw new InvalidDataException("Ein direkter übergeordneter DXBC-Container besitzt eine ungültige Ausgangsprüfsumme.");
                AddRange(allowedChanges, ancestor.Offset + 4, 16);
                DxbcContainer.UpdateChecksum(modified, ancestor);
            }

            EnsureOnlyAllowedBytesChanged(originalImage, modified, allowedChanges);
            ValidatePatchedResult(originalImage, modified, targetEntries, changedParents, constantPatches);

            var modifiedPages = FindModifiedPages(originalImage, modified);
            var modifiedRanges = FindModifiedRanges(originalImage, modified);
            if (modifiedPages.Count == 0)
                throw new InvalidDataException("Der vorbereitete DWM-Patch enthält keine Änderungen.");
            if (modifiedRanges.Count == 0)
                throw new InvalidDataException("Der vorbereitete DWM-Patch enthält keine bytegenauen Schreibbereiche.");

            report = new DwmRewriteReport
            {
                PatchedShaderCount = targetEntries.Count,
                PatchedConstantVectorCount = patchedConstants,
                ParentLibraryCount = changedParents.Count,
                ModifiedPageCount = modifiedPages.Count,
                ChangedByteCount = modifiedRanges.Sum(range => range.Length)
            };
            report.ModifiedPageOffsets.AddRange(modifiedPages);
            report.ModifiedRanges.AddRange(modifiedRanges);
            return modified;
        }

        public static void ValidateSupportedSettings(CalibrationSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            settings.Validate();
            if (!settings.ApplySdrGammaCorrection)
                throw new NotSupportedException(
                    "Der sichere DWM-Pfad benötigt ein aktiviertes SDR-Zielgamma; ohne Zielgamma wird die Korrektur entfernt.");
            if (!Close(settings.Contrast, 1.0)
                || !Close(settings.Saturation, 1.0)
                || !Close(settings.RedGain, 1.0)
                || !Close(settings.GreenGain, 1.0)
                || !Close(settings.BlueGain, 1.0))
                throw new NotSupportedException(
                    "Der sichere, ABI-erhaltende DWM-Pfad unterstützt derzeit Gamma und SDR-Helligkeit. " +
                    "Kontrast, Sättigung und RGB-Balance bleiben gesperrt, weil dafür der gesamte Windows-Shader ersetzt werden müsste.");
        }

        public static IReadOnlyList<int> FindModifiedPages(byte[] original, byte[] modified)
        {
            if (original == null || modified == null || original.Length != modified.Length)
                throw new ArgumentException("Die zu vergleichenden Modulabbilder müssen gleich groß sein.");

            var result = new List<int>();
            for (var pageOffset = 0; pageOffset < original.Length; pageOffset += PageSize)
            {
                var length = Math.Min(PageSize, original.Length - pageOffset);
                var changed = false;
                for (var index = 0; index < length; index++)
                {
                    if (original[pageOffset + index] == modified[pageOffset + index]) continue;
                    changed = true;
                    break;
                }
                if (changed) result.Add(pageOffset);
            }
            return result;
        }

        public static IReadOnlyList<DwmWriteRange> FindModifiedRanges(byte[] original, byte[] modified)
        {
            if (original == null || modified == null || original.Length != modified.Length)
                throw new ArgumentException("Die zu vergleichenden Modulabbilder müssen gleich groß sein.");

            var result = new List<DwmWriteRange>();
            var index = 0;
            while (index < original.Length)
            {
                while (index < original.Length && original[index] == modified[index]) index++;
                if (index >= original.Length) break;
                var start = index;
                while (index < original.Length && original[index] != modified[index]) index++;
                result.Add(new DwmWriteRange { Offset = start, Length = index - start });
            }
            return result;
        }

        private static IReadOnlyList<ConstantPatch> BuildConstantPatches(CalibrationSettings settings)
        {
            var brightnessFactor = 1.0 + settings.Brightness;
            if (brightnessFactor <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(settings.Brightness));
            // After the other three substitutions the Windows transfer becomes
            // pow(encoded * scale, gamma). This scale produces an exact linear-output
            // multiplier of (1 + Brightness).
            var scale = checked((float)Math.Pow(brightnessFactor, 1.0 / settings.Gamma));
            return new[]
            {
                new ConstantPatch { Name = "Exponent 2,4", From = OriginalExponent, To = checked((float)settings.Gamma) },
                new ConstantPatch { Name = "sRGB-Knick 0,04045", From = OriginalBreakpoint, To = 0.0f },
                new ConstantPatch { Name = "sRGB-Offset 0,055", From = OriginalOffset, To = 0.0f },
                new ConstantPatch { Name = "sRGB-Skalierung 1/1,055", From = OriginalScale, To = scale }
            };
        }

        private static IReadOnlyList<int> FindFloatTriple(byte[] image, int offset, int length, float value)
        {
            var scalar = BitConverter.GetBytes(value);
            var needle = new byte[12];
            for (var index = 0; index < 3; index++)
                Buffer.BlockCopy(scalar, 0, needle, index * 4, 4);

            var result = new List<int>();
            var end = checked(offset + length - needle.Length);
            for (var candidate = offset; candidate <= end; candidate++)
            {
                var match = true;
                for (var index = 0; index < needle.Length; index++)
                {
                    if (image[candidate + index] == needle[index]) continue;
                    match = false;
                    break;
                }
                if (!match) continue;
                result.Add(candidate);
                candidate += needle.Length - 1;
            }
            return result;
        }

        private static void WriteFloatTriple(byte[] image, int offset, float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0.0f)
                throw new InvalidDataException("Eine berechnete SDR-Shaderkonstante ist ungültig.");
            var scalar = BitConverter.GetBytes(value);
            for (var index = 0; index < 3; index++)
                Buffer.BlockCopy(scalar, 0, image, offset + index * 4, 4);
        }

        private static bool FloatTripleEquals(byte[] image, int offset, float value)
        {
            var scalar = BitConverter.GetBytes(value);
            for (var component = 0; component < 3; component++)
            {
                for (var index = 0; index < scalar.Length; index++)
                {
                    if (image[offset + component * 4 + index] != scalar[index]) return false;
                }
            }
            return true;
        }

        private static void ValidatePatchedResult(
            byte[] original,
            byte[] modified,
            IReadOnlyList<ValidatedDwmShader> targets,
            IReadOnlyList<DxbcBlob> parents,
            IReadOnlyList<ConstantPatch> constantPatches)
        {
            foreach (var entry in targets)
            {
                if (!DxbcContainer.TryParse(modified, entry.Blob.Offset, out var patched)
                    || patched.Size != entry.Blob.Size
                    || !DxbcContainer.VerifyChecksum(modified, patched))
                    throw new InvalidDataException("Ein konstantengepatchter SDR-Shader konnte nicht gültig zurückgelesen werden.");
                if (!entry.Blob.Chunks.Select(chunk => chunk.FourCc)
                    .SequenceEqual(patched.Chunks.Select(chunk => chunk.FourCc)))
                    throw new InvalidDataException("Der Konstantenpatch hat die DXBC-Chunkstruktur verändert.");

                for (var index = 0; index < entry.Blob.Chunks.Count; index++)
                {
                    var before = entry.Blob.Chunks[index];
                    var after = patched.Chunks[index];
                    if (before.HeaderOffset - entry.Blob.Offset != after.HeaderOffset - patched.Offset
                        || before.DataOffset - entry.Blob.Offset != after.DataOffset - patched.Offset
                        || before.DataSize != after.DataSize)
                        throw new InvalidDataException("Der Konstantenpatch hat Offset oder Größe eines DXBC-Chunks verändert.");
                }

                // Aon9 exists only in downlevel profiles. The manifest has
                // already validated each profile's exact chunk list.
                foreach (var fourCc in entry.Blob.Chunks.Select(chunk => chunk.FourCc).Where(name => name != "SHDR"))
                    EnsureChunkBytesEqual(original, entry.Blob.GetChunk(fourCc), modified, patched.GetChunk(fourCc), fourCc);

                var shaderChunk = patched.GetChunk("SHDR");
                foreach (var constantPatch in constantPatches)
                {
                    var expected = FindFloatTriple(modified, shaderChunk.DataOffset, shaderChunk.DataSize, constantPatch.To);
                    if (expected.Count < 1)
                        throw new InvalidDataException("Eine erwartete gepatchte SHDR-Konstante fehlt nach dem Rücklesen.");
                }
                D3DCompiler.ValidateDxbc(DxbcContainer.Slice(modified, patched), "konstantengepatchter SDR-Shader");
            }

            foreach (var originalParent in parents)
            {
                if (!DxbcContainer.TryParse(modified, originalParent.Offset, out var patchedParent)
                    || patchedParent.Size != originalParent.Size
                    || !DxbcContainer.VerifyChecksum(modified, patchedParent))
                    throw new InvalidDataException("Eine konstantengepatchte DWM-Profilbibliothek ist ungültig.");
                var specifications = targets.Where(target => target.Parent.Offset == originalParent.Offset)
                    .Select(target => target.Specification).ToList();
                DwmCoreInspector.ValidateParentReflection(modified, patchedParent, specifications);
                D3DCompiler.ValidateDxbc(DxbcContainer.Slice(modified, patchedParent), "konstantengepatchte DWM-Profilbibliothek");
            }
        }

        private static void EnsureChunkBytesEqual(
            byte[] original,
            DxbcChunk originalChunk,
            byte[] modified,
            DxbcChunk modifiedChunk,
            string fourCc)
        {
            if (originalChunk == null || modifiedChunk == null || originalChunk.DataSize != modifiedChunk.DataSize)
                throw new InvalidDataException(fourCc + " fehlt oder hat seine Größe verändert.");
            for (var index = 0; index < originalChunk.DataSize; index++)
            {
                if (original[originalChunk.DataOffset + index] == modified[modifiedChunk.DataOffset + index]) continue;
                throw new InvalidDataException(fourCc + " wurde verändert; der Konstantenpatch darf nur SHDR anfassen.");
            }
        }

        private static void EnsureOnlyAllowedBytesChanged(byte[] original, byte[] modified, HashSet<int> allowed)
        {
            for (var index = 0; index < original.Length; index++)
            {
                if (original[index] == modified[index] || allowed.Contains(index)) continue;
                throw new InvalidDataException(
                    "Der SDR-Konstantenpatch würde ein nicht freigegebenes Byte bei Moduloffset 0x" +
                    index.ToString("X") + " verändern.");
            }
        }

        private static void AddRange(ICollection<int> target, int offset, int length)
        {
            for (var index = 0; index < length; index++) target.Add(offset + index);
        }

        private static bool Close(double value, double expected)
        {
            return Math.Abs(value - expected) < 0.000001;
        }
    }
}
