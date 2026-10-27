using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using LiteDB;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

namespace Issue_2722_MapperCloneAttributes;

internal static class Program
{
    // Only the three cold Global opt-in cases may fail on the known-bad package.
    // Exit 0 proves that exact defect, 2 proves every case correct, 1 is a harness failure.
    private static int Main()
    {
        var host = ReproHostClient.CreateDefault();
        ReproConfigurationReporter.SendConfiguration(host);
        var original = BsonMapper.Global;
        var directory = Path.Combine(Path.GetTempPath(), "mapper-clone-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            // Published development packages require this acknowledgement; source may omit it.
            typeof(LiteDatabase).Assembly.GetType("LiteDB.LiteDBPragmas")
                ?.GetMethod("I_AM_AWARE_MY_DATABASE_BREAKS_WHEN_I_USE_THIS")?.Invoke(null, null);
            var failures = new List<string>();
            foreach (var mode in new[] { "types", "names", "annotations", "builtin", "fluent" })
            foreach (var warm in new[] { false, true })
            foreach (var explicitMapper in new[] { false, true })
            {
                var mapper = new BsonMapper();
                switch (mode)
                {
                    case "types":
                        mapper.RegisterIdAttribute(typeof(CustomIdAttribute));
                        mapper.RegisterIgnoreAttribute(typeof(CustomIgnoreAttribute));
                        break;
                    case "names":
                        mapper.RegisterIdAttributeByName(typeof(CustomIdAttribute).FullName!);
                        mapper.RegisterIgnoreAttributeByName(typeof(CustomIgnoreAttribute).FullName!);
                        break;
                    case "annotations": mapper.UseDataAnnotations(); break;
                    case "fluent": mapper.Entity<Row>().Id(x => x.Key, false).Ignore(x => x.Secret); break;
                }

                BsonMapper.Global = mapper;
                var name = $"{mode}-warm-{warm}-explicit-{explicitMapper}";
                var path = Path.Combine(directory, name + ".db");
                var document = mode == "builtin"
                    ? Persist(path, mapper, warm, explicitMapper, new BuiltInRow { Key = 42, Secret = "private" })
                    : Persist(path, mapper, warm, explicitMapper, new Row { Key = 42, Secret = "private" });
                var correct = document["_id"].Equals(new BsonValue(42)) &&
                    !document.ContainsKey("Key") && !document.ContainsKey("Secret") && document.Count == 1;
                Console.WriteLine($"{name}: correct={correct}, BSON={document}");
                if (correct) continue;

                // Reject a partial fix or unrelated persistence failure as proof of neither state.
                var expectedBug = !warm && !explicitMapper &&
                    (mode == "types" || mode == "names" || mode == "annotations") &&
                    document["_id"].IsObjectId && document["Key"].Equals(new BsonValue(42)) &&
                    document["Secret"].Equals(new BsonValue("private")) && document.Count == 3;
                if (!expectedBug) throw new InvalidOperationException("Unexpected BSON in " + name);
                failures.Add(name);
            }

            if (failures.Count != 0 && failures.Count != 3)
                throw new InvalidOperationException("Partial failure: " + string.Join(", ", failures));
            var reproduced = failures.Count == 3;
            var message = reproduced ? "CLONE_DEFECT_VERIFIED: 3 cold Global failures; 17 controls pass."
                : "FIXED_VERIFIED: all 20 persisted-BSON cases pass.";
            Console.WriteLine(message);
            host.SendResult(reproduced, message);
            return reproduced ? 0 : 2;
        }
        catch (Exception error)
        {
            host.SendResult(false, "Unexpected repro failure.", new { Exception = error.ToString() });
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            BsonMapper.Global = original;
            Directory.Delete(directory, true);
        }
    }

    private static BsonDocument Persist<T>(string path, BsonMapper mapper, bool warm, bool explicitMapper, T row)
    {
        if (warm) mapper.ToDocument(row);
        using (var database = new LiteDatabase(path, explicitMapper ? mapper : null))
        {
            database.GetCollection<T>("rows").Insert(row);
        }
        using var reopened = new LiteDatabase(path, new BsonMapper());
        return reopened.GetCollection("rows").FindAll().Single();
    }

    public sealed class CustomIdAttribute : Attribute
    {
    }

    public sealed class CustomIgnoreAttribute : Attribute
    {
    }

    public class Row
    {
        [CustomId, Key]
        public int Key { get; set; }
        [CustomIgnore, NotMapped]
        public string? Secret { get; set; }
    }

    public class BuiltInRow
    {
        [BsonId(false)]
        public int Key { get; set; }
        [BsonIgnore]
        public string? Secret { get; set; }
    }
}
