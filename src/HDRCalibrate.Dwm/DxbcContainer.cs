using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

// The DXBC checksum routine is a C# adaptation of AMD GPUOpen ShaderUtils'
// DXBCChecksum implementation and the RSA MD5 message-digest algorithm.
// Required notices are reproduced in THIRD-PARTY-NOTICES.md.

namespace HDRCalibrate.Dwm
{
    public sealed class DxbcChunk
    {
        public string FourCc { get; internal set; }
        public int HeaderOffset { get; internal set; }
        public int DataOffset { get; internal set; }
        public int DataSize { get; internal set; }
    }

    public sealed class DxbcBlob
    {
        public int Offset { get; internal set; }
        public int Size { get; internal set; }
        public IReadOnlyList<DxbcChunk> Chunks { get; internal set; }

        public int EndOffset => checked(Offset + Size);

        public DxbcChunk GetChunk(string fourCc)
        {
            return Chunks.FirstOrDefault(chunk => string.Equals(chunk.FourCc, fourCc, StringComparison.Ordinal));
        }
    }

    public static class DxbcContainer
    {
        private const int HeaderSize = 32;
        private const int HashOffset = 0x14;
        private const int MaximumChunkCount = 4096;
        private static readonly uint[] K = new uint[64];
        private static readonly int[] Shift =
        {
            7,12,17,22, 7,12,17,22, 7,12,17,22, 7,12,17,22,
            5, 9,14,20, 5, 9,14,20, 5, 9,14,20, 5, 9,14,20,
            4,11,16,23, 4,11,16,23, 4,11,16,23, 4,11,16,23,
            6,10,15,21, 6,10,15,21, 6,10,15,21, 6,10,15,21
        };

        static DxbcContainer()
        {
            for (var index = 0; index < K.Length; index++)
                K[index] = (uint)(Math.Abs(Math.Sin(index + 1.0)) * 4294967296.0);
        }

        public static IReadOnlyList<DxbcBlob> FindAll(byte[] image)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));
            var result = new List<DxbcBlob>();
            for (var offset = 0; offset <= image.Length - HeaderSize; offset++)
            {
                if (!HasMagic(image, offset)) continue;
                if (TryParse(image, offset, out var blob)) result.Add(blob);
            }
            return result;
        }

        public static bool TryParse(byte[] image, int offset, out DxbcBlob blob)
        {
            blob = null;
            if (image == null || offset < 0 || offset > image.Length - HeaderSize || !HasMagic(image, offset))
                return false;

            var reserved = ReadUInt32(image, offset + 20);
            var sizeValue = ReadUInt32(image, offset + 24);
            var chunkCountValue = ReadUInt32(image, offset + 28);
            if (reserved != 1 || sizeValue < HeaderSize || sizeValue > int.MaxValue || chunkCountValue > MaximumChunkCount)
                return false;

            var size = (int)sizeValue;
            var chunkCount = (int)chunkCountValue;
            if ((long)offset + size > image.Length || HeaderSize + (long)chunkCount * 4 > size)
                return false;

            var chunks = new List<DxbcChunk>(chunkCount);
            for (var index = 0; index < chunkCount; index++)
            {
                var relativeValue = ReadUInt32(image, offset + HeaderSize + index * 4);
                if (relativeValue > int.MaxValue) return false;
                var relative = (int)relativeValue;
                if (relative < HeaderSize || relative > size - 8) return false;

                var headerOffset = checked(offset + relative);
                var dataSizeValue = ReadUInt32(image, headerOffset + 4);
                if (dataSizeValue > int.MaxValue) return false;
                var dataSize = (int)dataSizeValue;
                var dataOffset = checked(headerOffset + 8);
                if ((long)dataOffset + dataSize > (long)offset + size) return false;

                chunks.Add(new DxbcChunk
                {
                    FourCc = Encoding.ASCII.GetString(image, headerOffset, 4),
                    HeaderOffset = headerOffset,
                    DataOffset = dataOffset,
                    DataSize = dataSize
                });
            }

            blob = new DxbcBlob { Offset = offset, Size = size, Chunks = chunks };
            return true;
        }

        public static bool VerifyChecksum(byte[] image, DxbcBlob blob)
        {
            if (blob == null) throw new ArgumentNullException(nameof(blob));
            var computed = ComputeChecksum(image, blob.Offset, blob.Size);
            for (var index = 0; index < computed.Length; index++)
            {
                if (image[blob.Offset + 4 + index] != computed[index]) return false;
            }
            return true;
        }

        public static void UpdateChecksum(byte[] image, DxbcBlob blob)
        {
            if (blob == null) throw new ArgumentNullException(nameof(blob));
            var checksum = ComputeChecksum(image, blob.Offset, blob.Size);
            Buffer.BlockCopy(checksum, 0, image, blob.Offset + 4, checksum.Length);
        }

        public static byte[] GetStoredChecksum(byte[] image, DxbcBlob blob)
        {
            var result = new byte[16];
            Buffer.BlockCopy(image, blob.Offset + 4, result, 0, result.Length);
            return result;
        }

        public static string GetStoredChecksumHex(byte[] image, DxbcBlob blob)
        {
            return ToHex(GetStoredChecksum(image, blob));
        }

        public static bool ContainsAscii(byte[] image, DxbcBlob blob, string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            var needle = Encoding.ASCII.GetBytes(value);
            var end = blob.EndOffset - needle.Length;
            for (var offset = blob.Offset; offset <= end; offset++)
            {
                var matches = true;
                for (var index = 0; index < needle.Length; index++)
                {
                    if (image[offset + index] == needle[index]) continue;
                    matches = false;
                    break;
                }
                if (matches) return true;
            }
            return false;
        }

        public static byte[] Slice(byte[] image, DxbcBlob blob)
        {
            var result = new byte[blob.Size];
            Buffer.BlockCopy(image, blob.Offset, result, 0, result.Length);
            return result;
        }

        public static string ToHex(byte[] bytes)
        {
            return BitConverter.ToString(bytes).Replace("-", string.Empty);
        }

        internal static void WriteUInt32(byte[] image, int offset, uint value)
        {
            var bytes = BitConverter.GetBytes(value);
            Buffer.BlockCopy(bytes, 0, image, offset, bytes.Length);
        }

        private static bool HasMagic(byte[] image, int offset)
        {
            return image[offset] == (byte)'D'
                && image[offset + 1] == (byte)'X'
                && image[offset + 2] == (byte)'B'
                && image[offset + 3] == (byte)'C';
        }

        private static uint ReadUInt32(byte[] image, int offset)
        {
            return BitConverter.ToUInt32(image, offset);
        }

        // Port of AMD GPUOpen's DXBC checksum routine. It is derived from the
        // RSA Data Security, Inc. MD5 Message Digest Algorithm; notices are in
        // THIRD-PARTY-NOTICES.md.
        private static byte[] ComputeChecksum(byte[] image, int offset, int size)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));
            if (offset < 0 || size < HeaderSize || (long)offset + size > image.Length)
                throw new ArgumentOutOfRangeException(nameof(size));

            var payloadLength = size - HashOffset;
            var bitLength = checked((uint)(payloadLength * 8));
            var state = new[] { 0x67452301u, 0xefcdab89u, 0x98badcfeu, 0x10325476u };
            var payloadOffset = offset + HashOffset;
            var fullLength = payloadLength & ~63;
            for (var index = 0; index < fullLength; index += 64)
                Transform(state, image, payloadOffset + index);

            var tailLength = payloadLength - fullLength;
            if (tailLength >= 56)
            {
                var block = new byte[64];
                Buffer.BlockCopy(image, payloadOffset + fullLength, block, 0, tailLength);
                block[tailLength] = 0x80;
                Transform(state, block, 0);

                var finalBlock = new byte[64];
                Buffer.BlockCopy(BitConverter.GetBytes(bitLength), 0, finalBlock, 0, 4);
                Buffer.BlockCopy(BitConverter.GetBytes((bitLength >> 2) | 1u), 0, finalBlock, 60, 4);
                Transform(state, finalBlock, 0);
            }
            else
            {
                var block = new byte[64];
                Buffer.BlockCopy(BitConverter.GetBytes(bitLength), 0, block, 0, 4);
                Buffer.BlockCopy(image, payloadOffset + fullLength, block, 4, tailLength);
                block[4 + tailLength] = 0x80;
                Buffer.BlockCopy(BitConverter.GetBytes((bitLength >> 2) | 1u), 0, block, 60, 4);
                Transform(state, block, 0);
            }

            var result = new byte[16];
            for (var index = 0; index < state.Length; index++)
                Buffer.BlockCopy(BitConverter.GetBytes(state[index]), 0, result, index * 4, 4);
            return result;
        }

        private static void Transform(uint[] state, byte[] buffer, int offset)
        {
            var a = state[0];
            var b = state[1];
            var c = state[2];
            var d = state[3];
            var aa = a;
            var bb = b;
            var cc = c;
            var dd = d;
            var words = new uint[16];
            for (var index = 0; index < words.Length; index++)
                words[index] = BitConverter.ToUInt32(buffer, offset + index * 4);

            unchecked
            {
                for (var index = 0; index < 64; index++)
                {
                    uint f;
                    int g;
                    if (index < 16)
                    {
                        f = (bb & cc) | (~bb & dd);
                        g = index;
                    }
                    else if (index < 32)
                    {
                        f = (dd & bb) | (~dd & cc);
                        g = (5 * index + 1) % 16;
                    }
                    else if (index < 48)
                    {
                        f = bb ^ cc ^ dd;
                        g = (3 * index + 5) % 16;
                    }
                    else
                    {
                        f = cc ^ (bb | ~dd);
                        g = (7 * index) % 16;
                    }

                    f = f + aa + K[index] + words[g];
                    var oldB = bb;
                    aa = dd;
                    dd = cc;
                    cc = bb;
                    bb = oldB + RotateLeft(f, Shift[index]);
                }

                state[0] = a + aa;
                state[1] = b + bb;
                state[2] = c + cc;
                state[3] = d + dd;
            }
        }

        private static uint RotateLeft(uint value, int count)
        {
            return (value << count) | (value >> (32 - count));
        }
    }
}
