using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiteDB.Engine;

namespace LiteDB.Tests.Engine
{
    // Independent raw-page oracle: never invokes allocation, recovery or free-list repair.
    // Uses public decryption/BSON only, so the same oracle can inspect a known-bad package.
    internal static class AllocationFileGraph
    {
        internal static uint Verify(string filename, string password)
        {
            using var file = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using Stream stream = password == null ? file : new AesStream(password, file, allowRecovery: false);
            const int size = 8192;
            if (stream.Length % size != 0) throw new InvalidDataException("partial physical page");
            var header = Read(stream, 0);
            var last = BitConverter.ToUInt32(header, 64);
            if ((last + 1L) * size > stream.Length) throw new InvalidDataException("LastPageID beyond file");
            var pages = new Dictionary<uint, byte[]>();
            for (uint id = 0; id <= last; id++)
            {
                var bytes = Read(stream, id);
                if (BitConverter.ToUInt32(bytes, 0) != id) throw new InvalidDataException("page identity at " + id);
                pages.Add(id, bytes);
            }
            var free = new HashSet<uint>();
            var next = BitConverter.ToUInt32(header, 60);
            while (next != uint.MaxValue)
            {
                if (!free.Add(next) || !pages.TryGetValue(next, out var page) || page[4] != 0)
                    throw new InvalidDataException("invalid free chain at " + next);
                next = BitConverter.ToUInt32(page, 9);
            }
            var empties = pages.Where(p => p.Value[4] == 0).Select(p => p.Key);
            if (!free.SetEquals(empties)) throw new InvalidDataException("unlisted empty pages");
            var map = BsonSerializer.Deserialize(header.Skip(192).ToArray());
            var collections = new HashSet<uint>(map.Values.Select(v => checked((uint)v.AsInt32)));
            foreach (var id in collections)
                if (!pages.ContainsKey(id) || pages[id][4] != 2) throw new InvalidDataException("collection root");
            foreach (var pair in pages.Where(p => p.Key != 0 && p.Value[4] != 0))
            {
                var col = BitConverter.ToUInt32(pair.Value, 19);
                if (!collections.Contains(col)) throw new InvalidDataException("unreachable collection owner " + pair.Key);
                if (pair.Value[4] < 2 || pair.Value[4] > 6) throw new InvalidDataException("invalid page type");
            }
            return last;
        }

        private static byte[] Read(Stream stream, uint id)
        {
            var page = new byte[8192];
            stream.Position = id * 8192L;
            var offset = 0;
            while (offset < page.Length)
            {
                var count = stream.Read(page, offset, page.Length - offset);
                if (count == 0) throw new EndOfStreamException();
                offset += count;
            }
            return page;
        }
    }
}
