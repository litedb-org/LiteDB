using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using LiteDB;

namespace LiteDB.Fuzz.Legacy
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            var seed = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 2947;
            var count = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 100;
            var random = new StableRandom(seed);
            try
            {
                for (var step = 0; step < count; step++) RunCase(random, step);
                Console.WriteLine("PASS legacy-net481 seed={0} count={1} arch={2}",
                    seed, count, Environment.Is64BitProcess ? "x64" : "x86");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("FAIL legacy-net481 seed={0}: {1}", seed, error);
                return 1;
            }
        }

        private static void RunCase(Random random, int step)
        {
            var documents = Enumerable.Range(1, random.Next(8, 80)).Select(id => new BsonDocument
            {
                ["_id"] = id,
                ["score"] = random.Next(-30, 31),
                ["text"] = Text(random, random.Next(0, 300)),
                ["bytes"] = Bytes(random, random.Next(0, 300)),
                ["values"] = new BsonArray(random.Next(-4, 5), random.Next(-4, 5))
            }).ToArray();

            foreach (var document in documents)
            {
                var bytes = BsonSerializer.Serialize(document);
                var copy = BsonSerializer.Deserialize(bytes);
                Require(BsonSerializer.Serialize(copy).SequenceEqual(bytes), "BSON round trip changed bytes");
                var truncated = bytes.Take(random.Next(1, bytes.Length)).ToArray();
                var rejected = false;
                try { BsonSerializer.Deserialize(truncated); }
                catch (LiteException) { rejected = true; }
                catch (ArgumentException) { rejected = true; }
                Require(rejected, "truncated BSON was accepted");
            }

            using (var stream = new MemoryStream())
            using (var db = new LiteDatabase(stream))
            {
                var rows = db.GetCollection("rows");
                rows.InsertBulk(documents);
                rows.EnsureIndex("score");
                rows.EnsureIndex("values", "values[*]");
                var low = random.Next(-30, 20);
                var expected = documents.Where(item => item["score"].AsInt32 >= low)
                    .OrderBy(item => item["score"]).ThenBy(item => item["_id"])
                    .Select(item => item["_id"].AsInt32).ToArray();
                var actual = rows.Query().Where("score >= @0", low).OrderBy("score").ThenBy("_id")
                    .ToArray().Select(item => item["_id"].AsInt32).ToArray();
                Require(expected.SequenceEqual(actual), "netstandard index/sort result disagreed with LINQ oracle");
                if (step % 7 == 0)
                {
                    rows.UpdateMany(BsonExpression.Create("{ score: score + 1 }"),
                        BsonExpression.Create("values[*] ANY = @0", 0));
                    Require(rows.Count() == documents.Length, "bulk update changed row count");
                }
            }
        }

        private static byte[] Bytes(Random random, int length)
        {
            var bytes = new byte[length];
            random.NextBytes(bytes);
            return bytes;
        }

        private static string Text(Random random, int length)
        {
            var alphabet = new[] { 'a', 'Z', '\0', 'é', '\u0301', 'ı', '中' };
            return new string(Enumerable.Range(0, length)
                .Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidDataException(message);
        }

        private sealed class StableRandom : Random
        {
            private uint _state;

            internal StableRandom(int seed)
            {
                _state = unchecked((uint)seed);
                if (_state == 0) _state = 0x6d2b79f5;
            }

            private uint NextWord()
            {
                var value = _state;
                value ^= value << 13;
                value ^= value >> 17;
                value ^= value << 5;
                return _state = value;
            }

            public override int Next() { return (int)(NextWord() % int.MaxValue); }
            public override int Next(int maxValue) { return maxValue == 0 ? 0 : (int)(NextWord() % (uint)maxValue); }
            public override int Next(int minValue, int maxValue)
            {
                return minValue + (int)(NextWord() % (uint)(maxValue - minValue));
            }
            public override void NextBytes(byte[] buffer)
            {
                for (var i = 0; i < buffer.Length; i++) buffer[i] = (byte)NextWord();
            }
            protected override double Sample() { return NextWord() / ((double)uint.MaxValue + 1); }
        }
    }
}
