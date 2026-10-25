using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using LiteDB;
using LiteDB.Vector;

// Regenerates the entries of Vectors_6_0_0_prerelease_114.zip (LiteDB-Artifacts
// compatibility/fixtures, pinned by LiteDB.Tests/Resources/artifacts.json) with the
// published 6.0.0-prerelease.114 package:
//   dotnet run --project tools/PrereleaseVectorFixture -- <empty-output-directory>
// vectors.db and vectors-encrypted.db are written as is; vector-salvage.db is written, closed and
// then has the BSON length of one string value overwritten. As originally, the two steps run in
// processes of their own (the fixed-seed skip-list randomizer starts afresh in each).
internal static class Program
{
    private static int Main(string[] args)
    {
        // The originals ran with LANG unset (invariant culture, stored as collation LCID 127).
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        if (args.Length == 3 && args[0] == "--child")
        {
            if (args[1] == "vectors") Vectors(args[2]);
            else if (args[1] == "vector-salvage") VectorSalvage(args[2]);
            else throw new ArgumentException("unknown child mode " + args[1]);
            return 0;
        }
        if (args.Length != 1)
        {
            Console.Error.WriteLine("usage: PrereleaseVectorFixture <empty-output-directory>");
            return 2;
        }
        var dir = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(dir);
        if (Directory.EnumerateFileSystemEntries(dir).Any())
        {
            Console.Error.WriteLine("output directory is not empty: " + dir);
            return 2;
        }
        Console.WriteLine($"LiteDB {typeof(LiteDatabase).Assembly.GetName().Version} on .NET {Environment.Version}");

        RunChild("vectors", dir);
        RunChild("vector-salvage", Path.Combine(dir, "vector-salvage.db"));

        foreach (var file in Directory.GetFiles(dir).OrderBy(x => x, StringComparer.Ordinal))
        {
            using var stream = File.OpenRead(file);
            Console.WriteLine($"{Path.GetFileName(file)} {stream.Length} sha256={Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant()}");
        }
        return 0;
    }

    private static void RunChild(string mode, string path)
    {
        var host = Environment.ProcessPath ?? "dotnet";
        var psi = new ProcessStartInfo(host) { UseShellExecute = false };
        if (string.Equals(Path.GetFileNameWithoutExtension(host), "dotnet", StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var arg in new[] { "--child", mode, path }) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("could not start " + host);
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"child {mode} exited with {process.ExitCode}");
    }

    /// <summary>
    /// vectors.db and vectors-encrypted.db (password "vector-secret"): collection "docs" with 40
    /// documents, index "name" and vector index "embedding" on $.Embedding; collection "computed"
    /// with 20 documents (every fifth Embedding null) and vector index "coalesced".
    /// </summary>
    private static void Vectors(string dir)
    {
        foreach (var password in new[] { (string)null, "vector-secret" })
        {
            var name = Path.Combine(dir, password == null ? "vectors.db" : "vectors-encrypted.db");
            var cs = "Filename=" + name + (password == null ? "" : ";Password=" + password);
            using (var db = new LiteDatabase(cs))
            {
                var docs = db.GetCollection("docs");
                docs.Insert(Enumerable.Range(1, 40).Select(i => new BsonDocument
                {
                    ["_id"] = i, ["name"] = "d" + i, ["Embedding"] = new BsonVector(new[] { (float)i, 1f })
                }));
                docs.EnsureIndex("name");
                docs.EnsureIndex("embedding", "$.Embedding", new VectorIndexOptions(2));
                var computed = db.GetCollection("computed");
                computed.Insert(Enumerable.Range(1, 20).Select(i => new BsonDocument
                {
                    ["_id"] = i, ["Embedding"] = i % 5 == 0 ? BsonValue.Null : new BsonVector(new[] { 1f, (float)i })
                }));
                computed.EnsureIndex("coalesced", "COALESCE($.Embedding, [0, 0])", new VectorIndexOptions(2));
                var top = docs.Query().TopKNear("Embedding", new[] { 20f, 1f }, 1).ToArray();
                var indexes = db.GetCollection("$indexes").FindAll()
                    .Select(x => $"{x["collection"].AsString}.{x["name"].AsString}:{x["expression"].AsString}");
                Console.WriteLine($"{Path.GetFileName(name)}: top={top.Single()["_id"]} indexes={string.Join(",", indexes)}");
            }
            var bytes = File.ReadAllBytes(name);
            Console.WriteLine($"  file version byte (plain only meaningful) = {bytes[59]} length={bytes.Length}");
        }
    }

    /// <summary>
    /// vector-salvage.db: collection "vectors" with {_id: 1..3, a: "keep-i", b: "u-i", v: [i, 1]} and
    /// vector index "vv" on an expression that reads $.b; checkpointed; then the BSON length of "b"
    /// of document 2 is overwritten with 0x7FFFFFF0.
    /// </summary>
    private static void VectorSalvage(string name)
    {
        const string Expression = "IIF(SUBSTRING(COALESCE($.b, 'x'), 1, 2) = '-0', $.v, $.v)";
        using (var db = new LiteDatabase(name))
        {
            var c = db.GetCollection("vectors");
            c.Insert(Enumerable.Range(1, 3).Select(i => new BsonDocument
            {
                ["_id"] = i, ["a"] = "keep-" + i, ["b"] = "u-" + i, ["v"] = new BsonVector(new[] { (float)i, 1f })
            }));
            c.EnsureIndex("vv", Expression, new VectorIndexOptions(2));
            db.Checkpoint();
            Console.WriteLine("indexes: " + string.Join(",", db.GetCollection("$indexes").FindAll().Select(x => x["name"].AsString)));
        }
        var bytes = File.ReadAllBytes(name);
        Console.WriteLine("file version byte = " + bytes[59]);
        var pattern = new byte[] { 0x02, (byte)'b', 0 }.Concat(BitConverter.GetBytes(4)).Concat(Encoding.UTF8.GetBytes("u-2\0")).ToArray();
        var hits = Enumerable.Range(0, bytes.Length - pattern.Length).Where(p => bytes.AsSpan(p, pattern.Length).SequenceEqual(pattern)).ToArray();
        Console.WriteLine("damaged: " + hits.Length);
        if (hits.Length != 1) throw new InvalidOperationException("expected one document 2");
        foreach (var at in hits) BitConverter.GetBytes(0x7FFFFFF0).CopyTo(bytes, at + 3);
        File.WriteAllBytes(name, bytes);
    }
}
