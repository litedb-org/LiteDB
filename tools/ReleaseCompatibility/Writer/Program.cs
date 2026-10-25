using System;
using System.IO;
using System.Linq;
using LiteDB;

internal static class Program
{
    private static void Main(string[] args)
    {
        var settings = new ConnectionString(args[0]) { Password = args[1] == "encrypted" ? Corpus.Password : null };
#if !V4
        Collation.Default = args[1] == "encrypted" ? new Collation("en-US/IgnoreCase") : Collation.Binary;
#endif
        using (var db = new LiteDatabase(settings))
        {
            foreach (var name in Corpus.Collections)
            {
                var rows = db.GetCollection(name);
                rows.EnsureIndex("sequence", true);
                rows.EnsureIndex("code", true);
                rows.EnsureIndex("text");
#if V4
                rows.EnsureIndex("tags");
#else
                rows.EnsureIndex("tags", "$.tags[*]");
                rows.EnsureIndex("lower_code", "LOWER($.code)");
#endif
                rows.Insert(Enumerable.Range(0, Corpus.Count).Reverse().Select(i => Corpus.Document(name, i)));
            }
        }
        using (var db = new LiteDatabase(settings))
        {
            foreach (var name in Corpus.Collections)
            {
                var rows = db.GetCollection(name);
                if (rows.FindAll().Count() != Corpus.Count) throw new Exception("Writer scan mismatch: " + name);
                // Validate every committed payload by primary-key seek. The current
                // reader separately checks complete scans, counts and all indexes.
                for (var i = 0; i < Corpus.Count; i++)
                {
                    // Fully drain queries: 5.0.18 leaks a transaction when its
                    // FindById/FirstOrDefault reader is disposed before exhaustion.
                    var actual = rows.Find(Query.EQ("_id", Corpus.Id(name, i))).ToArray();
                    if (actual.Length != 1 || !BsonSerializer.Serialize(actual[0]).SequenceEqual(BsonSerializer.Serialize(Corpus.Document(name, i))))
                        throw new Exception("Writer payload mismatch: " + name + "/" + i);
                }
            }
        }
        Console.WriteLine(typeof(LiteDatabase).Assembly.FullName + " verified " + args[0]);
    }
}
