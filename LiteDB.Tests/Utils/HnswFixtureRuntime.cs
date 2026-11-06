using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace LiteDB.Tests
{
    // Historical numeric-only graphs carry the producer's ICU signature. Preserve
    // every graph/document byte, but exercise legacy ordering validation on a copy
    // so the graph regression can run on NLS and other ICU versions too.
    public static class HnswFixtureRuntime
    {
        public static byte[] Prepare(byte[] original)
        {
            string digest;
            using (var hash = SHA256.Create())
                digest = BitConverter.ToString(hash.ComputeHash(original)).Replace("-", "").ToLowerInvariant();
            if (!new[] {
                "3c63d8063ffa50831e7f594507608f21405eec8f63089f56cfdda214774722c0",
                "f0f99c8b50ed7a47eb9786ba38a4ba1e261a6d5398b418766df1cd654c393781",
                "e28216f8fa75469147a0e64d674b9dcfec436b681b9ca17952551c9e56a05789",
                "d3042015c5a4bd6c5c23e9a1f57b41c27002596ade0173093bfe7d2396ed859d"
            }.Contains(digest)) throw new InvalidDataException("Not a pinned historical HNSW fixture.");
            var copy = (byte[])original.Clone();
            // EnginePragmas.P_COLLATION_STAMP, followed by the header CRC32C.
            Array.Clear(copy, 92, 4);
            Array.Clear(copy, 14, 4);
            uint crc = uint.MaxValue;
            for (var i = 0; i < 8192; i++)
            {
                crc ^= copy[i];
                for (var bit = 0; bit < 8; bit++)
                    crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0x82F63B78u);
            }
            Array.Copy(BitConverter.GetBytes(~crc), 0, copy, 14, 4);
            // Do not assert compatibility by re-stamping. The real engine must
            // validate legacy index ordering, and these inputs must remain numeric.
            using (var stream = new MemoryStream(copy, false))
            using (var db = new LiteDatabase(new LiteDB.Engine.LiteEngine(
                new LiteDB.Engine.EngineSettings { ReadOnly = true, DataStream = stream })))
            {
                foreach (var index in db.Execute("SELECT $ FROM $indexes").ToArray())
                    if (index["indexType"].AsInt32 == 0 && index["name"].AsString != "_id")
                        throw new InvalidDataException("HNSW fixture contains a secondary scalar index.");
                foreach (var collection in db.GetCollectionNames())
                    if (db.GetCollection(collection).FindAll().Any(x => !x["_id"].IsNumber))
                        throw new InvalidDataException("HNSW fixture contains a nonnumeric primary key.");
            }
            return copy;
        }

        public static void PrepareFile(string path) => File.WriteAllBytes(path, Prepare(File.ReadAllBytes(path)));
    }
}
