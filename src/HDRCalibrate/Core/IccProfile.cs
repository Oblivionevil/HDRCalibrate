using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace HDRCalibrate.Core
{
    public sealed class IccProfileInfo
    {
        public int Size { get; internal set; }
        public string Description { get; internal set; }
        public bool HasMhc2 { get; internal set; }
        public int LutEntryCount { get; internal set; }
        public double MinLuminanceNits { get; internal set; }
        public double PeakLuminanceNits { get; internal set; }
        public IReadOnlyList<string> Tags { get; internal set; }
    }

    public static class IccProfileInspector
    {
        public static IccProfileInfo Inspect(byte[] profile)
        {
            var document = IccDocument.Parse(profile);
            var result = new IccProfileInfo
            {
                Size = profile.Length,
                Description = document.ReadDescription(),
                HasMhc2 = document.Contains("MHC2"),
                Tags = document.Tags.Select(tag => tag.Signature).ToArray()
            };

            if (result.HasMhc2)
            {
                var mhc = Mhc2Calibration.Parse(document.Get("MHC2"));
                result.LutEntryCount = mhc.EntryCount;
                result.MinLuminanceNits = mhc.MinLuminanceNits;
                result.PeakLuminanceNits = mhc.PeakLuminanceNits;
            }

            return result;
        }
    }

    public static class IccProfileFactory
    {
        public static byte[] CreateAdjustedProfile(
            byte[] baseProfile,
            string description,
            CalibrationSettings settings,
            int requestedLutEntries,
            double sdrWhiteNits,
            DisplayColorMetadata fallbackMetadata)
        {
            if (baseProfile == null) throw new ArgumentNullException(nameof(baseProfile));
            if (string.IsNullOrWhiteSpace(description)) throw new ArgumentException("Eine Profilbeschreibung fehlt.", nameof(description));

            var document = IccDocument.Parse(baseProfile);
            var baseCalibration = document.Contains("MHC2")
                ? Mhc2Calibration.Parse(document.Get("MHC2"))
                : Mhc2Calibration.Identity(fallbackMetadata);

            var composed = Mhc2Calibration.Compose(
                baseCalibration,
                settings,
                requestedLutEntries,
                sdrWhiteNits);

            document.ReplaceOrAdd("MHC2", composed.ToTagBytes());
            document.ReplaceOrAdd("desc", IccTagFactory.CreateMluc(description));
            return document.Serialize(DateTime.UtcNow);
        }

        public static byte[] CreateFallbackProfile(
            string description,
            DisplayColorMetadata metadata,
            CalibrationSettings settings,
            int requestedLutEntries,
            double sdrWhiteNits)
        {
            if (metadata == null || !metadata.IsUsable())
                throw new ArgumentException("Die Display-Farbmetadaten sind unvollständig.", nameof(metadata));

            var document = IccDocument.CreateEmpty();
            var colorants = IccColorimetry.CreateColorants(metadata);

            document.ReplaceOrAdd("cprt", IccTagFactory.CreateMluc("Copyright HDR Calibrate - generated profile"));
            document.ReplaceOrAdd("rTRC", IccTagFactory.CreateGammaCurve(2.2));
            document.ReplaceOrAdd("gTRC", IccTagFactory.CreateGammaCurve(2.2));
            document.ReplaceOrAdd("bTRC", IccTagFactory.CreateGammaCurve(2.2));
            document.ReplaceOrAdd("chad", IccTagFactory.CreateSf32Matrix(colorants.ChromaticAdaptation));
            document.ReplaceOrAdd("rXYZ", IccTagFactory.CreateXyz(colorants.Red));
            document.ReplaceOrAdd("gXYZ", IccTagFactory.CreateXyz(colorants.Green));
            document.ReplaceOrAdd("bXYZ", IccTagFactory.CreateXyz(colorants.Blue));
            document.ReplaceOrAdd("wtpt", IccTagFactory.CreateXyz(new[] { 0.9642, 1.0, 0.8249 }));
            document.ReplaceOrAdd("lumi", IccTagFactory.CreateXyz(new[] { 0.0, metadata.MaxFullFrameLuminanceNits, 0.0 }));
            document.ReplaceOrAdd("desc", IccTagFactory.CreateMluc(description));

            var calibration = Mhc2Calibration.Compose(
                Mhc2Calibration.Identity(metadata),
                settings,
                requestedLutEntries,
                sdrWhiteNits);
            document.ReplaceOrAdd("MHC2", calibration.ToTagBytes());
            return document.Serialize(DateTime.UtcNow);
        }
    }

    internal sealed class Mhc2Calibration
    {
        public int EntryCount { get; private set; }
        public double MinLuminanceNits { get; private set; }
        public double PeakLuminanceNits { get; private set; }
        public Matrix3x3 Matrix { get; private set; }
        public double[][] Curves { get; private set; }

        public static Mhc2Calibration Identity(DisplayColorMetadata metadata)
        {
            var safe = metadata != null && metadata.IsUsable() ? metadata : new DisplayColorMetadata();
            return new Mhc2Calibration
            {
                EntryCount = 2,
                MinLuminanceNits = safe.MinLuminanceNits,
                PeakLuminanceNits = safe.MaxLuminanceNits,
                Matrix = Matrix3x3.Identity,
                Curves = new[]
                {
                    new[] { 0.0, 1.0 },
                    new[] { 0.0, 1.0 },
                    new[] { 0.0, 1.0 }
                }
            };
        }

        public static Mhc2Calibration Parse(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 36 || IccBinary.Signature(bytes, 0) != "MHC2")
                throw new InvalidDataException("Das MHC2-Tag ist ungültig.");

            var count = checked((int)IccBinary.ReadUInt32(bytes, 8));
            if (count < 0 || count > 4096)
                throw new InvalidDataException("Das MHC2-Tag enthält eine ungültige LUT-Größe.");

            var matrixOffset = checked((int)IccBinary.ReadUInt32(bytes, 20));
            var redOffset = checked((int)IccBinary.ReadUInt32(bytes, 24));
            var greenOffset = checked((int)IccBinary.ReadUInt32(bytes, 28));
            var blueOffset = checked((int)IccBinary.ReadUInt32(bytes, 32));

            var matrix = Matrix3x3.Identity;
            if (matrixOffset != 0)
            {
                IccBinary.RequireRange(bytes, matrixOffset, 48);
                matrix = new Matrix3x3(
                    IccBinary.ReadS15Fixed16(bytes, matrixOffset),
                    IccBinary.ReadS15Fixed16(bytes, matrixOffset + 4),
                    IccBinary.ReadS15Fixed16(bytes, matrixOffset + 8),
                    IccBinary.ReadS15Fixed16(bytes, matrixOffset + 16),
                    IccBinary.ReadS15Fixed16(bytes, matrixOffset + 20),
                    IccBinary.ReadS15Fixed16(bytes, matrixOffset + 24),
                    IccBinary.ReadS15Fixed16(bytes, matrixOffset + 32),
                    IccBinary.ReadS15Fixed16(bytes, matrixOffset + 36),
                    IccBinary.ReadS15Fixed16(bytes, matrixOffset + 40));
            }

            var curves = new[]
            {
                ReadCurve(bytes, redOffset, count),
                ReadCurve(bytes, greenOffset, count),
                ReadCurve(bytes, blueOffset, count)
            };

            return new Mhc2Calibration
            {
                EntryCount = count == 0 ? 2 : count,
                MinLuminanceNits = IccBinary.ReadS15Fixed16(bytes, 12),
                PeakLuminanceNits = IccBinary.ReadS15Fixed16(bytes, 16),
                Matrix = matrix,
                Curves = curves
            };
        }

        public static Mhc2Calibration Compose(
            Mhc2Calibration baseCalibration,
            CalibrationSettings settings,
            int requestedLutEntries,
            double sdrWhiteNits)
        {
            settings.Validate();
            var count = Math.Max(2, Math.Min(4096, requestedLutEntries));
            var peak = baseCalibration.PeakLuminanceNits;
            if (peak < 80.0 || peak > 10000.0) peak = 1000.0;

            var adjustment = HdrToneCurve.BuildLut(settings, count, sdrWhiteNits, peak);
            var curves = new double[3][];
            for (var channel = 0; channel < 3; channel++)
            {
                curves[channel] = new double[count];
                for (var index = 0; index < count; index++)
                {
                    curves[channel][index] = Interpolate(baseCalibration.Curves[channel], adjustment[index]);
                }

                EnforceMonotonic(curves[channel]);
            }

            var userMatrix = ColorAdjustmentMatrix.CreateXyzAdjustment(settings);
            return new Mhc2Calibration
            {
                EntryCount = count,
                MinLuminanceNits = Math.Max(0.0, baseCalibration.MinLuminanceNits),
                PeakLuminanceNits = peak,
                Matrix = baseCalibration.Matrix.Multiply(userMatrix),
                Curves = curves
            };
        }

        public byte[] ToTagBytes()
        {
            const int headerSize = 36;
            const int matrixSize = 48;
            var curveSize = checked(8 + EntryCount * 4);
            var redOffset = headerSize + matrixSize;
            var greenOffset = redOffset + curveSize;
            var blueOffset = greenOffset + curveSize;
            var result = new byte[blueOffset + curveSize];

            IccBinary.WriteSignature(result, 0, "MHC2");
            IccBinary.WriteUInt32(result, 8, (uint)EntryCount);
            IccBinary.WriteS15Fixed16(result, 12, MinLuminanceNits);
            IccBinary.WriteS15Fixed16(result, 16, PeakLuminanceNits);
            IccBinary.WriteUInt32(result, 20, headerSize);
            IccBinary.WriteUInt32(result, 24, (uint)redOffset);
            IccBinary.WriteUInt32(result, 28, (uint)greenOffset);
            IccBinary.WriteUInt32(result, 32, (uint)blueOffset);

            var matrixOffset = headerSize;
            for (var row = 0; row < 3; row++)
            {
                for (var column = 0; column < 4; column++)
                {
                    var value = column == 3 ? 0.0 : Matrix[row, column];
                    IccBinary.WriteS15Fixed16(result, matrixOffset, value);
                    matrixOffset += 4;
                }
            }

            WriteCurve(result, redOffset, Curves[0]);
            WriteCurve(result, greenOffset, Curves[1]);
            WriteCurve(result, blueOffset, Curves[2]);
            return result;
        }

        private static double[] ReadCurve(byte[] bytes, int offset, int count)
        {
            if (offset == 0 || count == 0) return new[] { 0.0, 1.0 };
            var size = checked(8 + count * 4);
            IccBinary.RequireRange(bytes, offset, size);
            if (IccBinary.Signature(bytes, offset) != "sf32")
                throw new InvalidDataException("Eine MHC2-LUT besitzt nicht den Typ sf32.");

            var result = new double[count];
            for (var index = 0; index < count; index++)
            {
                result[index] = Clamp01(IccBinary.ReadS15Fixed16(bytes, offset + 8 + index * 4));
            }

            EnforceMonotonic(result);
            return result;
        }

        private static void WriteCurve(byte[] bytes, int offset, double[] curve)
        {
            IccBinary.WriteSignature(bytes, offset, "sf32");
            for (var index = 0; index < curve.Length; index++)
            {
                IccBinary.WriteS15Fixed16(bytes, offset + 8 + index * 4, Clamp01(curve[index]));
            }
        }

        private static double Interpolate(double[] curve, double value)
        {
            if (curve == null || curve.Length < 2) return Clamp01(value);
            var position = Clamp01(value) * (curve.Length - 1);
            var lower = (int)Math.Floor(position);
            var upper = Math.Min(curve.Length - 1, lower + 1);
            var fraction = position - lower;
            return curve[lower] * (1.0 - fraction) + curve[upper] * fraction;
        }

        private static void EnforceMonotonic(double[] curve)
        {
            if (curve.Length == 0) return;
            curve[0] = Clamp01(curve[0]);
            for (var index = 1; index < curve.Length; index++)
            {
                curve[index] = Math.Max(curve[index - 1], Clamp01(curve[index]));
            }
        }

        private static double Clamp01(double value) => Math.Max(0.0, Math.Min(1.0, value));
    }

    internal sealed class IccDocument
    {
        private readonly byte[] _header;
        private readonly List<IccTag> _tags;

        public IReadOnlyList<IccTag> Tags => _tags;

        private IccDocument(byte[] header, List<IccTag> tags)
        {
            _header = header;
            _tags = tags;
        }

        public static IccDocument Parse(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 132)
                throw new InvalidDataException("Das ICC-Profil ist zu klein.");
            if (IccBinary.Signature(bytes, 36) != "acsp")
                throw new InvalidDataException("Die ICC-Signatur acsp fehlt.");

            var declaredSize = checked((int)IccBinary.ReadUInt32(bytes, 0));
            if (declaredSize < 132 || declaredSize > bytes.Length)
                throw new InvalidDataException("Die ICC-Profilgröße ist ungültig.");
            var count = checked((int)IccBinary.ReadUInt32(bytes, 128));
            if (count < 0 || count > 256 || 132L + count * 12L > declaredSize)
                throw new InvalidDataException("Die ICC-Tagtabelle ist ungültig.");

            var tags = new List<IccTag>(count);
            for (var index = 0; index < count; index++)
            {
                var tableOffset = 132 + index * 12;
                var signature = IccBinary.Signature(bytes, tableOffset);
                var offset = checked((int)IccBinary.ReadUInt32(bytes, tableOffset + 4));
                var length = checked((int)IccBinary.ReadUInt32(bytes, tableOffset + 8));
                IccBinary.RequireRange(bytes, offset, length, declaredSize);
                var payload = new byte[length];
                Buffer.BlockCopy(bytes, offset, payload, 0, length);
                tags.Add(new IccTag(signature, payload));
            }

            var header = new byte[128];
            Buffer.BlockCopy(bytes, 0, header, 0, header.Length);
            return new IccDocument(header, tags);
        }

        public static IccDocument CreateEmpty()
        {
            var header = new byte[128];
            IccBinary.WriteUInt32(header, 8, 0x04400000);
            IccBinary.WriteSignature(header, 12, "mntr");
            IccBinary.WriteSignature(header, 16, "RGB ");
            IccBinary.WriteSignature(header, 20, "XYZ ");
            IccBinary.WriteSignature(header, 36, "acsp");
            IccBinary.WriteSignature(header, 40, "MSFT");
            IccBinary.WriteUInt32(header, 64, 1);
            IccBinary.WriteS15Fixed16(header, 68, 0.9642);
            IccBinary.WriteS15Fixed16(header, 72, 1.0);
            IccBinary.WriteS15Fixed16(header, 76, 0.8249);
            IccBinary.WriteSignature(header, 80, "HDCL");
            return new IccDocument(header, new List<IccTag>());
        }

        public bool Contains(string signature) => _tags.Any(tag => tag.Signature == signature);

        public byte[] Get(string signature)
        {
            var tag = _tags.FirstOrDefault(candidate => candidate.Signature == signature);
            if (tag == null) throw new KeyNotFoundException(signature);
            return tag.Data;
        }

        public void ReplaceOrAdd(string signature, byte[] data)
        {
            if (signature == null || signature.Length != 4) throw new ArgumentException(nameof(signature));
            if (data == null) throw new ArgumentNullException(nameof(data));
            var existing = _tags.FindIndex(tag => tag.Signature == signature);
            var replacement = new IccTag(signature, (byte[])data.Clone());
            if (existing >= 0) _tags[existing] = replacement;
            else _tags.Add(replacement);
        }

        public string ReadDescription()
        {
            var tag = _tags.FirstOrDefault(candidate => candidate.Signature == "desc");
            if (tag == null || tag.Data.Length < 12) return string.Empty;
            var type = IccBinary.Signature(tag.Data, 0);
            if (type == "mluc" && tag.Data.Length >= 28)
            {
                var length = checked((int)IccBinary.ReadUInt32(tag.Data, 20));
                var offset = checked((int)IccBinary.ReadUInt32(tag.Data, 24));
                IccBinary.RequireRange(tag.Data, offset, length);
                return Encoding.BigEndianUnicode.GetString(tag.Data, offset, length).TrimEnd('\0');
            }

            if (type == "desc")
            {
                var length = checked((int)IccBinary.ReadUInt32(tag.Data, 8));
                length = Math.Min(length, Math.Max(0, tag.Data.Length - 12));
                return Encoding.ASCII.GetString(tag.Data, 12, length).TrimEnd('\0');
            }

            return string.Empty;
        }

        public byte[] Serialize(DateTime utcTimestamp)
        {
            var tagTableLength = checked(4 + _tags.Count * 12);
            var dataOffset = Align4(128 + tagTableLength);
            var offsets = new int[_tags.Count];
            var totalSize = dataOffset;
            for (var index = 0; index < _tags.Count; index++)
            {
                offsets[index] = totalSize;
                totalSize = checked(Align4(totalSize + _tags[index].Data.Length));
            }

            var result = new byte[totalSize];
            Buffer.BlockCopy(_header, 0, result, 0, 128);
            IccBinary.WriteUInt32(result, 0, (uint)totalSize);
            IccBinary.WriteSignature(result, 36, "acsp");
            WriteDate(result, utcTimestamp);
            Array.Clear(result, 84, 16);
            IccBinary.WriteUInt32(result, 128, (uint)_tags.Count);

            for (var index = 0; index < _tags.Count; index++)
            {
                var tableOffset = 132 + index * 12;
                IccBinary.WriteSignature(result, tableOffset, _tags[index].Signature);
                IccBinary.WriteUInt32(result, tableOffset + 4, (uint)offsets[index]);
                IccBinary.WriteUInt32(result, tableOffset + 8, (uint)_tags[index].Data.Length);
                Buffer.BlockCopy(_tags[index].Data, 0, result, offsets[index], _tags[index].Data.Length);
            }

            WriteProfileId(result);
            return result;
        }

        private static void WriteDate(byte[] bytes, DateTime timestamp)
        {
            var utc = timestamp.Kind == DateTimeKind.Utc ? timestamp : timestamp.ToUniversalTime();
            IccBinary.WriteUInt16(bytes, 24, (ushort)utc.Year);
            IccBinary.WriteUInt16(bytes, 26, (ushort)utc.Month);
            IccBinary.WriteUInt16(bytes, 28, (ushort)utc.Day);
            IccBinary.WriteUInt16(bytes, 30, (ushort)utc.Hour);
            IccBinary.WriteUInt16(bytes, 32, (ushort)utc.Minute);
            IccBinary.WriteUInt16(bytes, 34, (ushort)utc.Second);
        }

        private static void WriteProfileId(byte[] profile)
        {
            var copy = (byte[])profile.Clone();
            Array.Clear(copy, 44, 4);
            Array.Clear(copy, 64, 4);
            Array.Clear(copy, 84, 16);
            using (var md5 = MD5.Create())
            {
                var hash = md5.ComputeHash(copy);
                Buffer.BlockCopy(hash, 0, profile, 84, 16);
            }
        }

        private static int Align4(int value) => checked((value + 3) & ~3);
    }

    internal sealed class IccTag
    {
        public string Signature { get; }
        public byte[] Data { get; }

        public IccTag(string signature, byte[] data)
        {
            Signature = signature;
            Data = data;
        }
    }

    internal static class IccTagFactory
    {
        public static byte[] CreateMluc(string text)
        {
            var encoded = Encoding.BigEndianUnicode.GetBytes(text ?? string.Empty);
            var result = new byte[28 + encoded.Length];
            IccBinary.WriteSignature(result, 0, "mluc");
            IccBinary.WriteUInt32(result, 8, 1);
            IccBinary.WriteUInt32(result, 12, 12);
            result[16] = (byte)'d'; result[17] = (byte)'e';
            result[18] = (byte)'D'; result[19] = (byte)'E';
            IccBinary.WriteUInt32(result, 20, (uint)encoded.Length);
            IccBinary.WriteUInt32(result, 24, 28);
            Buffer.BlockCopy(encoded, 0, result, 28, encoded.Length);
            return result;
        }

        public static byte[] CreateGammaCurve(double gamma)
        {
            var result = new byte[16];
            IccBinary.WriteSignature(result, 0, "curv");
            IccBinary.WriteUInt32(result, 8, 1);
            var encoded = (ushort)Math.Max(1, Math.Min(65535, Math.Round(gamma * 256.0)));
            IccBinary.WriteUInt16(result, 12, encoded);
            return result;
        }

        public static byte[] CreateXyz(double[] xyz)
        {
            var result = new byte[20];
            IccBinary.WriteSignature(result, 0, "XYZ ");
            IccBinary.WriteS15Fixed16(result, 8, xyz[0]);
            IccBinary.WriteS15Fixed16(result, 12, xyz[1]);
            IccBinary.WriteS15Fixed16(result, 16, xyz[2]);
            return result;
        }

        public static byte[] CreateSf32Matrix(Matrix3x3 matrix)
        {
            var result = new byte[44];
            IccBinary.WriteSignature(result, 0, "sf32");
            var offset = 8;
            for (var row = 0; row < 3; row++)
            {
                for (var column = 0; column < 3; column++)
                {
                    IccBinary.WriteS15Fixed16(result, offset, matrix[row, column]);
                    offset += 4;
                }
            }
            return result;
        }
    }

    internal sealed class IccColorants
    {
        public double[] Red { get; set; }
        public double[] Green { get; set; }
        public double[] Blue { get; set; }
        public Matrix3x3 ChromaticAdaptation { get; set; }
    }

    internal static class IccColorimetry
    {
        public static IccColorants CreateColorants(DisplayColorMetadata metadata)
        {
            var red = XyToXyz(metadata.RedX, metadata.RedY);
            var green = XyToXyz(metadata.GreenX, metadata.GreenY);
            var blue = XyToXyz(metadata.BlueX, metadata.BlueY);
            var white = XyToXyz(metadata.WhiteX, metadata.WhiteY);
            var primaryMatrix = Matrix3x3.FromColumns(red, green, blue);
            var scale = primaryMatrix.Inverse().Transform(white[0], white[1], white[2]);
            red = Scale(red, scale[0]);
            green = Scale(green, scale[1]);
            blue = Scale(blue, scale[2]);

            var bradford = new Matrix3x3(
                 0.8951,  0.2664, -0.1614,
                -0.7502,  1.7135,  0.0367,
                 0.0389, -0.0685,  1.0296);
            var sourceCone = bradford.Transform(white[0], white[1], white[2]);
            var destinationWhite = new[] { 0.9642, 1.0, 0.8249 };
            var destinationCone = bradford.Transform(destinationWhite[0], destinationWhite[1], destinationWhite[2]);
            var diagonal = new Matrix3x3(
                destinationCone[0] / sourceCone[0], 0, 0,
                0, destinationCone[1] / sourceCone[1], 0,
                0, 0, destinationCone[2] / sourceCone[2]);
            var adaptation = bradford.Inverse().Multiply(diagonal).Multiply(bradford);

            return new IccColorants
            {
                Red = adaptation.Transform(red[0], red[1], red[2]),
                Green = adaptation.Transform(green[0], green[1], green[2]),
                Blue = adaptation.Transform(blue[0], blue[1], blue[2]),
                ChromaticAdaptation = adaptation
            };
        }

        private static double[] XyToXyz(double x, double y)
        {
            return new[] { x / y, 1.0, (1.0 - x - y) / y };
        }

        private static double[] Scale(double[] vector, double scale)
        {
            return new[] { vector[0] * scale, vector[1] * scale, vector[2] * scale };
        }
    }

    internal static class IccBinary
    {
        public static uint ReadUInt32(byte[] bytes, int offset)
        {
            RequireRange(bytes, offset, 4);
            return ((uint)bytes[offset] << 24)
                 | ((uint)bytes[offset + 1] << 16)
                 | ((uint)bytes[offset + 2] << 8)
                 | bytes[offset + 3];
        }

        public static double ReadS15Fixed16(byte[] bytes, int offset)
        {
            return unchecked((int)ReadUInt32(bytes, offset)) / 65536.0;
        }

        public static string Signature(byte[] bytes, int offset)
        {
            RequireRange(bytes, offset, 4);
            return Encoding.ASCII.GetString(bytes, offset, 4);
        }

        public static void WriteUInt16(byte[] bytes, int offset, ushort value)
        {
            RequireRange(bytes, offset, 2);
            bytes[offset] = (byte)(value >> 8);
            bytes[offset + 1] = (byte)value;
        }

        public static void WriteUInt32(byte[] bytes, int offset, uint value)
        {
            RequireRange(bytes, offset, 4);
            bytes[offset] = (byte)(value >> 24);
            bytes[offset + 1] = (byte)(value >> 16);
            bytes[offset + 2] = (byte)(value >> 8);
            bytes[offset + 3] = (byte)value;
        }

        public static void WriteS15Fixed16(byte[] bytes, int offset, double value)
        {
            var clamped = Math.Max(-32768.0, Math.Min(32767.9999847412, value));
            var fixedValue = (int)Math.Round(clamped * 65536.0, MidpointRounding.AwayFromZero);
            WriteUInt32(bytes, offset, unchecked((uint)fixedValue));
        }

        public static void WriteSignature(byte[] bytes, int offset, string signature)
        {
            if (signature == null || signature.Length != 4)
                throw new ArgumentException("Eine ICC-Signatur muss vier Zeichen lang sein.", nameof(signature));
            RequireRange(bytes, offset, 4);
            var encoded = Encoding.ASCII.GetBytes(signature);
            Buffer.BlockCopy(encoded, 0, bytes, offset, 4);
        }

        public static void RequireRange(byte[] bytes, int offset, int length, int declaredLength = -1)
        {
            var limit = declaredLength < 0 ? bytes?.Length ?? 0 : Math.Min(bytes?.Length ?? 0, declaredLength);
            if (bytes == null || offset < 0 || length < 0 || (long)offset + length > limit)
                throw new InvalidDataException("Ein ICC-Datenbereich liegt außerhalb der Datei.");
        }
    }
}
