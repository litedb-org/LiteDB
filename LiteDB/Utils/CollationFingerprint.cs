using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LiteDB
{
    internal static class CollationFingerprint
    {
        internal static uint Compute(Collation collation)
        {
            using (var data = new MemoryStream())
            using (var writer = new BinaryWriter(data, Encoding.UTF8, true))
            {
                writer.Write("LiteDB collation v1");
                // BSON ObjectId timestamps and PID bytes compare as unsigned values.
                // This affects every collation, including Ordinal.
                writer.Write("unsigned ObjectId ordering v1");
                writer.Write("canonical document ordering v1");
                writer.Write((int)collation.SortOptions);
                // Ordinal comparison is independent of runtime sort tables/culture.
                if (collation.SortOptions != CompareOptions.Ordinal)
                {
                    // Nested arrays/documents now use this collation recursively.
                    // Older stamped indexes used binary ordering for their contents.
                    writer.Write("recursive nested collation v2");
                    writer.Write(collation.LCID);
                    SortVersion version;
                    try { version = collation.Culture.CompareInfo.Version; }
                    // Older Mono exposes the API but cannot provide a sort version.
                    // Keep zero: every open then validates actual index ordering.
                    catch (NotImplementedException) { return 0; }
                    catch (NotSupportedException) { return 0; }
                    writer.Write(version.FullVersion);
                    writer.Write(version.SortId.ToByteArray());
                    // Also distinguish invariant mode and implementations that report
                    // identical versions but disagree on ordering or equivalence.
                    var samples = new[] { "a", "A", "ä", "ae", "v", "w", "i", "I", "ı", "İ", "-", "_", "é", "e\u0301", "ss", "ß", "中", "", "\0" };
                    foreach (var left in samples)
                    foreach (var right in samples)
                        writer.Write((sbyte)collation.Compare(left, right));
                }
                writer.Flush();
                using (var sha = SHA256.Create())
                {
                    var hash = sha.ComputeHash(data.ToArray());
                    var stamp = (uint)(hash[0] | hash[1] << 8 | hash[2] << 16 | hash[3] << 24);
                    return stamp == 0 ? 1u : stamp;
                }
            }
        }
    }
}
