using System.Buffers.Binary;
using System.IO.Compression;
using LiteDB;
using LiteDB.Tests;
using LiteDB.Vector;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

namespace Issue_3141_IncomingOnlyDelete;

internal static class Program
{
    private static int Main()
    {
        var host = ReproHostClient.CreateDefault();
        ReproConfigurationReporter.SendConfiguration(host);
        typeof(LiteDatabase).Assembly.GetType("LiteDB.LiteDBPragmas")
            ?.GetMethod("I_AM_AWARE_MY_DATABASE_BREAKS_WHEN_I_USE_THIS")?.Invoke(null, null);
        var directory = Path.Combine(Path.GetTempPath(), "hnswd-inbound-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var file = Path.Combine(directory, "vectors.db");
            using (var zip = ZipFile.OpenRead(ArtifactFixtures.Path("HnswAsymmetric_6b2ecd3af.zip")))
                zip.GetEntry("vectors.db")!.ExtractToFile(file);
            using (var db = new LiteDatabase(file))
            {
                if (db.GetCollection("docs").Count() != 73 || HasDanglingLink(db))
                    throw new InvalidOperationException("Fixture control failed.");
                if (!db.GetCollection("docs").Delete(2)) throw new InvalidOperationException("Delete failed.");
                db.Checkpoint();
            }
            bool reproduced;
            using (var db = new LiteDatabase(file))
            {
                var docs = db.GetCollection("docs");
                if (docs.Count() != 72 || docs.FindById(2) != null) throw new InvalidOperationException("Document control failed.");
                reproduced = HasDanglingLink(db);
                if (!reproduced && docs.Query().TopKNear("Embedding", new[] { 1f, 2f }, 73).ToArray()
                    .Any(x => x["_id"].AsInt32 == 2)) throw new InvalidOperationException("Deleted endpoint returned.");
            }
            var message = reproduced ? "BUG_VERIFIED" : "FIXED_VERIFIED";
            Console.WriteLine(message);
            host.SendResult(reproduced, message);
            return reproduced ? 0 : 2;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            host.SendResult(false, "Unexpected harness or database failure", new { Exception = error.ToString() });
            return 1;
        }
        finally { Directory.Delete(directory, true); }
    }

    // Independent reader for the pinned legacy layout, through the public dump
    // API. Graph reachability/ANN recall cannot certify slot allocation.
    private static bool HasDanglingLink(LiteDatabase db)
    {
        var allocated = new HashSet<(uint Page, byte Slot)>();
        var links = new List<(uint Page, byte Slot)>();
        var pages = db.Execute("SELECT $ FROM $dump WHERE $.collection = 'docs' AND $.pageType = 'VectorIndex'").ToArray();
        foreach (var page in pages)
        {
            var id = (uint)page["pageID"].AsInt32;
            var bytes = db.Execute($"SELECT $ FROM $dump({id})").Single()["buffer"].AsBinary;
            if (bytes.Length != 8192) throw new InvalidOperationException("Unexpected page size.");
            for (var slot = 0; slot < 255; slot++)
            {
                var footer = 8192 - (slot + 1) * 4;
                var length = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(footer, 2));
                var position = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(footer + 2, 2));
                if (slot > bytes[30] || bytes[30] == 255 || length == 0) continue;
                allocated.Add((id, (byte)slot));
                for (var level = 0; level < 4; level++)
                {
                    var start = position + 6 + level * 41;
                    var count = bytes[start];
                    if (count > 8) throw new InvalidOperationException("Invalid fixture link count.");
                    for (var link = 0; link < count; link++)
                    {
                        var offset = start + 1 + link * 5;
                        links.Add((BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4)), bytes[offset + 4]));
                    }
                }
            }
        }
        return links.Any(link => !allocated.Contains(link));
    }
}
