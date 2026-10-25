using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using LiteDB.Engine;

namespace LiteDB.Client.Shared
{
    // Immutable little-endian ABI, independently hashed in both envelope pages.
    internal sealed class SharedMemoryEnvelope
    {
        internal readonly long Size;
        internal readonly int Processes, Generations, Stride;
        internal readonly Guid Authority;
        internal readonly byte[] Bytes;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("LDBSHM2\0");
        internal const int HashOffset = 224;
        internal const int MaximumSize = 512 * 1024 * 1024;

        private SharedMemoryEnvelope(byte[] bytes)
        {
            Bytes = bytes;
            Size = BitConverter.ToInt64(bytes, 16);
            Processes = BitConverter.ToInt32(bytes, 24);
            Generations = BitConverter.ToInt32(bytes, 28);
            Stride = BitConverter.ToInt32(bytes, 32);
            Authority = new Guid(bytes.Skip(64).Take(16).ToArray());
        }

        internal static SharedMemoryEnvelope Create(EngineSettings settings, string identity)
        {
            if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("Shared ABI 2 requires little endian.");
            var processes = settings.SharedProcessCapacity;
            var generations = settings.SharedGenerationCapacity;
            if (processes < 1 || processes > 65536 || generations < 1 || generations > 65536)
                throw new ArgumentOutOfRangeException("Shared capacities must be between 1 and 65536.");
            var stride = checked((SharedMemoryLayout.ReaderOffset + generations * 8 + 4095) / 4096 * 4096);
            var minimum = checked(SharedMemoryLayout.Blocks + (long)stride * processes);
            var size = settings.SharedMemorySize == 0 ? minimum : settings.SharedMemorySize;
            if (size < minimum || size > MaximumSize || size % 4096 != 0)
                throw new ArgumentOutOfRangeException(nameof(settings.SharedMemorySize), "Shared map size must be page aligned, fit configured capacities and be at most 512 MiB.");
            var bytes = new byte[SharedMemoryLayout.EnvelopeBytes];
            Buffer.BlockCopy(Magic, 0, bytes, 0, Magic.Length);
            Put(bytes, 8, SharedMemoryLayout.Major);
            Put(bytes, 12, SharedMemoryLayout.EnvelopeBytes);
            Put(bytes, 16, size);
            Put(bytes, 24, processes);
            Put(bytes, 28, generations);
            Put(bytes, 32, stride);
            Put(bytes, 36, SharedMemoryLayout.Blocks);
            Put(bytes, 40, SharedMemoryLayout.Control);
            Put(bytes, 44, 8); // aligned atomic width
            Put(bytes, 48, 1L); // required capability: native OFD/range liveness
            Buffer.BlockCopy(Guid.NewGuid().ToByteArray(), 0, bytes, 64, 16);
            Buffer.BlockCopy(Hash(identity), 0, bytes, 80, 32);
            Buffer.BlockCopy(NamespaceHash(settings.Filename, settings.SharedMutexNameStrategy), 0, bytes, 112, 32);
            Buffer.BlockCopy(Hash(bytes.Take(HashOffset).ToArray()), 0, bytes, HashOffset, 32);
            return new SharedMemoryEnvelope(bytes);
        }

        internal static SharedMemoryEnvelope Decode(byte[] bytes, string path, string identity, bool quiescent = false, SharedMutexNameStrategy strategy = SharedMutexNameStrategy.Default)
        {
            if (bytes.Length != SharedMemoryLayout.EnvelopeBytes || !Magic.SequenceEqual(bytes.Take(8)))
                throw new IOException("Unknown Shared coordination magic.");
            if (BitConverter.ToInt32(bytes, 8) != SharedMemoryLayout.Major ||
                BitConverter.ToInt32(bytes, 12) != SharedMemoryLayout.EnvelopeBytes ||
                BitConverter.ToInt32(bytes, 36) != SharedMemoryLayout.Blocks ||
                BitConverter.ToInt32(bytes, 40) != SharedMemoryLayout.Control ||
                BitConverter.ToInt32(bytes, 44) != 8 || BitConverter.ToInt64(bytes, 48) != 1 ||
                BitConverter.ToInt64(bytes, 56) != 0 || bytes.Skip(144).Take(80).Any(b => b != 0))
                throw new IOException("Unsupported Shared coordination ABI/layout/capabilities.");
            if (!Hash(identity).SequenceEqual(bytes.Skip(80).Take(32)) || !NamespaceHash(path, strategy).SequenceEqual(bytes.Skip(112).Take(32)))
                throw new IOException("Shared coordination database identity/namespace mismatch.");
            var result = new SharedMemoryEnvelope(bytes);
            if (result.Processes < 1 || result.Processes > 65536 || result.Generations < 1 || result.Generations > 65536 ||
                result.Stride < SharedMemoryLayout.ReaderOffset + result.Generations * 8 || result.Stride % 4096 != 0 ||
                result.Size < SharedMemoryLayout.Blocks + (long)result.Processes * result.Stride ||
                result.Size > MaximumSize || result.Size % 4096 != 0 || result.Authority == Guid.Empty)
                throw new IOException("Invalid Shared coordination capacity/length.");
            if (!Hash(bytes.Take(HashOffset).ToArray()).SequenceEqual(bytes.Skip(HashOffset)) && !quiescent)
                throw new IOException("Damaged Shared coordination envelope checksum.");
            return result;
        }

        private static byte[] NamespaceHash(string path, SharedMutexNameStrategy strategy) =>
            Hash(path + "\0" + SharedMutexNameFactory.Create(path, strategy));
        internal static bool IsZero(byte[] bytes) => bytes.All(b => b == 0);
        internal static byte[] Hash(string value) => Hash(Encoding.UTF8.GetBytes(value));
        internal static byte[] Hash(byte[] value) { using var sha = SHA256.Create(); return sha.ComputeHash(value); }
        private static void Put(byte[] b, int p, int value) => Buffer.BlockCopy(BitConverter.GetBytes(value), 0, b, p, 4);
        private static void Put(byte[] b, int p, long value) => Buffer.BlockCopy(BitConverter.GetBytes(value), 0, b, p, 8);
    }
}
