using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace HDRCalibrate.Dwm
{
    internal static class DwmPatchPlanFingerprint
    {
        /// <param name="dwmCoreSha256">The verified whole-file hash of the patched build; binds the plan to it.</param>
        public static string Compute(string dwmCoreSha256, byte[] modifiedImage, IEnumerable<DwmWriteRange> ranges)
        {
            if (string.IsNullOrWhiteSpace(dwmCoreSha256)) throw new ArgumentNullException(nameof(dwmCoreSha256));
            if (modifiedImage == null) throw new ArgumentNullException(nameof(modifiedImage));
            if (ranges == null) throw new ArgumentNullException(nameof(ranges));

            var ordered = ranges.OrderBy(range => range.Offset).ToList();
            if (ordered.Count == 0) throw new InvalidDataException("Der DWM-Schreibplan ist leer.");

            using (var payload = new MemoryStream())
            using (var writer = new BinaryWriter(payload, Encoding.UTF8, true))
            {
                writer.Write(DwmPatchSafety.PatchImplementationId);
                writer.Write(dwmCoreSha256.ToUpperInvariant());
                writer.Write(ordered.Count);
                foreach (var range in ordered)
                {
                    if (range == null || range.Offset < 0 || range.Length <= 0
                        || range.Offset > modifiedImage.Length - range.Length)
                        throw new InvalidDataException("Ein Bereich des DWM-Schreibplans ist ungültig.");
                    writer.Write(range.Offset);
                    writer.Write(range.Length);
                    writer.Write(modifiedImage, range.Offset, range.Length);
                }
                writer.Flush();

                payload.Position = 0;
                using (var sha256 = SHA256.Create())
                    return string.Concat(sha256.ComputeHash(payload).Select(value => value.ToString("X2")));
            }
        }
    }
}
