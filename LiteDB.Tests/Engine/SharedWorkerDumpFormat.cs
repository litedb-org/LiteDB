using System;
using System.IO;

namespace LiteDB.Tests.Engine;

/// <summary>Bounded structural check of the full process dump needed for hang diagnosis.</summary>
internal static class SharedWorkerDumpFormat
{
    // Layouts: Microsoft MINIDUMP_HEADER, MINIDUMP_DIRECTORY, MINIDUMP_THREAD,
    // MINIDUMP_MEMORY64_LIST, MINIDUMP_SYSTEM_INFO and MINIDUMP_MISC_INFO.
    // This establishes retained evidence, not that every debugger can unwind it.
    internal static bool Validate(string path, int pid, int processBits)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            var length = (ulong)stream.Length;
            if (length < 32 || reader.ReadUInt32() != 0x504d444d ||
                (reader.ReadUInt32() & 0xffff) != 0xa793) return false;
            var count = reader.ReadUInt32();
            var directory = reader.ReadUInt32();
            stream.Position = 24;
            if ((reader.ReadUInt64() & 2) == 0 || count == 0 || count > 1024 ||
                !Fits(directory, count * 12UL, length)) return false;
            var threads = false;
            var modules = false;
            var memory = false;
            var system = false;
            var identity = false;
            for (var i = 0; i < count; i++)
            {
                stream.Position = directory + i * 12L;
                var type = reader.ReadUInt32();
                var size = reader.ReadUInt32();
                var position = reader.ReadUInt32();
                if (!Fits(position, size, length)) return false;
                stream.Position = position;
                switch (type)
                {
                    case 3: // ThreadListStream, including every saved context.
                        if (size < 4) return false;
                        var threadCount = reader.ReadUInt32();
                        if (threadCount == 0 || threadCount > 65536 || 4UL + threadCount * 48UL > size) return false;
                        for (var t = 0; t < threadCount; t++)
                        {
                            stream.Position = position + 4L + t * 48L + 40;
                            var contextSize = reader.ReadUInt32();
                            var contextRva = reader.ReadUInt32();
                            if (contextSize == 0 || !Fits(contextRva, contextSize, length)) return false;
                        }
                        threads = true;
                        break;
                    case 4: // ModuleListStream.
                        if (size < 4) return false;
                        var moduleCount = reader.ReadUInt32();
                        if (moduleCount == 0 || 4UL + moduleCount * 108UL > size) return false;
                        modules = true;
                        break;
                    case 9: // Memory64ListStream; all referenced bytes must exist.
                        if (size < 16) return false;
                        var ranges = reader.ReadUInt64();
                        var dataPosition = reader.ReadUInt64();
                        if (ranges == 0 || ranges > 1048576 || 16UL + ranges * 16UL > size) return false;
                        ulong total = 0;
                        for (ulong r = 0; r < ranges; r++)
                        {
                            reader.ReadUInt64(); // virtual address
                            var bytes = reader.ReadUInt64();
                            if (bytes > length || total > length - bytes) return false;
                            total += bytes;
                        }
                        if (total == 0 || !Fits(dataPosition, total, length)) return false;
                        memory = true;
                        break;
                    case 7: // SystemInfoStream: PROCESSOR_ARCHITECTURE_INTEL/AMD64.
                        if (size < 56 || reader.ReadUInt16() != (processBits == 32 ? 0 : 9)) return false;
                        system = true;
                        break;
                    case 15: // MiscInfoStream: process ID must be present and match.
                        if (size < 12 || reader.ReadUInt32() < 12 ||
                            (reader.ReadUInt32() & 1) == 0 || reader.ReadUInt32() != pid) return false;
                        identity = true;
                        break;
                }
            }
            return threads && modules && memory && system && identity;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool Fits(ulong position, ulong size, ulong length) =>
        position <= length && size <= length - position;
}
