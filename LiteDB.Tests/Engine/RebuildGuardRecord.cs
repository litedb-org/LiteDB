#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace LiteDB.Tests.Engine
{
    // Test-side encoding, deliberately independent of the recovery parser.
    internal static class RebuildGuardRecord
    {
        internal static string[] Decode(byte[] bytes)
        {
            if (bytes.Length < 32) return null;
            var length = bytes.Length - 32;
            using var sha = SHA256.Create();
            if (!sha.ComputeHash(bytes, 0, length).SequenceEqual(bytes.Skip(length))) return null;
            using var stream = new MemoryStream(bytes, 0, length);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            return Enumerable.Range(0, 8).Select(_ => reader.ReadString()).ToArray();
        }

        internal static byte[] Encode(string[] fields)
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
                foreach (var field in fields) writer.Write(field);
            var payload = stream.ToArray();
            using var sha = SHA256.Create();
            return payload.Concat(sha.ComputeHash(payload)).ToArray();
        }
    }
}
#endif
