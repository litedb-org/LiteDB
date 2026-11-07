using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class PrimaryKeyDeleteRange_Tests
    {
        [Theory]
        [InlineData("<", 0)] [InlineData("<=", 0)] [InlineData(">", 0)] [InlineData(">=", 0)]
        [InlineData("<", 1)] [InlineData("<=", 1)] [InlineData(">", 1)] [InlineData(">=", 1)]
        [InlineData("<", 2)] [InlineData("<=", 2)] [InlineData(">", 2)] [InlineData(">=", 2)]
        public void Range_matches_old_projection_path_across_pages_rollback_and_reopen(string op, int kind)
        {
            using var fastFile = new TempFile();
            using var oldFile = new TempFile();
            var collation = new Collation("en-US/IgnoreCase");
            var predicate = BsonExpression.Create("_id " + op + " @0", Id(450, kind));
            PrimaryKeyDeleteRange.TryCreate(predicate, collation, out _).Should().Be(kind != 2);
            using (var fast = Open(fastFile.Filename, collation))
            using (var old = Open(oldFile.Filename, collation))
            {
                Seed(fast, kind); Seed(old, kind);
                fast.BeginTrans(); old.BeginTrans();
                DeleteAndCompare(fast, old, predicate);
                fast.Rollback().Should().BeTrue(); old.Rollback().Should().BeTrue();
                AssertRows(fast, Enumerable.Range(1, 900), kind);
                DeleteAndCompare(fast, old, predicate);
                // Repeated deletes must not revisit released/reused nodes.
                fast.GetCollection("docs").DeleteMany(predicate).Should().Be(0);
            }
            for (var i = 0; i < 2; i++)
            {
                using var fast = Open(fastFile.Filename, collation);
                using var old = Open(oldFile.Filename, collation);
                Bytes(fast).Should().Equal(Bytes(old));
                AssertIndexes(fast);
                fast.GetCollection("docs").Insert(Document(1001 + i, kind));
                old.GetCollection("docs").Insert(Document(1001 + i, kind));
                fast.Checkpoint(); old.Checkpoint();
            }
        }

        [Theory]
        [InlineData("en-US/IgnoreCase", "b")]
        [InlineData("en-US/None", "B")]
        public void String_bounds_use_execution_collation(string culture, string bound)
        {
            var collation = new Collation(culture);
            using var fast = new LiteDatabase(new LiteEngine(new EngineSettings { DataStream = new MemoryStream(), Collation = collation }));
            using var old = new LiteDatabase(new LiteEngine(new EngineSettings { DataStream = new MemoryStream(), Collation = collation }));
            foreach (var db in new[] { fast, old })
                db.GetCollection("docs").Insert(new[] { "a", "B", "c", "D" }.Select((id, i) =>
                    new BsonDocument { ["_id"] = id, ["n"] = i, ["keep"] = true }));
            DeleteAndCompare(fast, old, BsonExpression.Create("_id <= @0", bound));
        }

        [Fact]
        public void Live_readers_keep_their_snapshot_during_delete_and_page_reuse()
        {
            using var file = new TempFile();
            using var db = Open(file.Filename, Collation.Default);
            Seed(db, 0);
            using var ready = new System.Threading.ManualResetEventSlim();
            using var done = new System.Threading.ManualResetEventSlim();
            var reader = Task.Run(() =>
            {
                using var cursor = db.GetCollection("docs").FindAll().GetEnumerator();
                cursor.MoveNext().Should().BeTrue();
                var ids = new System.Collections.Generic.List<int> { cursor.Current["n"].AsInt32 };
                ready.Set();
                done.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue();
                while (cursor.MoveNext()) ids.Add(cursor.Current["n"].AsInt32);
                ids.Should().BeEquivalentTo(Enumerable.Range(1, 900));
            });
            ready.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue();
            try
            {
                db.GetCollection("docs").DeleteMany("_id <= 450").Should().Be(450);
                db.GetCollection("docs").Insert(Enumerable.Range(1001, 450).Select(n => Document(n, 0)));
                db.Checkpoint();
            }
            finally { done.Set(); }
            reader.GetAwaiter().GetResult();
            AssertRows(db, Enumerable.Range(451, 450).Concat(Enumerable.Range(1001, 450)), 0);
        }

        [Theory]
        [InlineData("_id <= 5 AND n > 1")]
        [InlineData("_id <= 5 OR n = 8")]
        [InlineData("LOWER(_id) < 'm'")]
        [InlineData("_id IN [1,2]")]
        [InlineData("_id < null")]
        public void Unproven_shapes_use_existing_path(string expression)
        {
            PrimaryKeyDeleteRange.TryCreate(expression, Collation.Default, out _).Should().BeFalse();
        }

        [Fact]
        public void Read_transform_retains_query_callback_semantics()
        {
            var calls = 0;
            using var engine = new LiteEngine(new EngineSettings
            {
                DataStream = new MemoryStream(),
                ReadTransform = (collection, value) => { calls++; return value; }
            });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.GetCollection("docs").Insert(Enumerable.Range(1, 10).Select(n => Document(n, 0)));
            db.GetCollection("docs").DeleteMany("_id <= 5").Should().Be(5);
            calls.Should().BeGreaterThan(0);
            db.GetCollection("docs").Count().Should().Be(5);
        }

        [Fact]
        public void Same_thread_reader_retains_pre_write_snapshot()
        {
            using var file = new TempFile();
            using var db = Open(file.Filename, Collation.Default);
            Seed(db, 0);
            using var cursor = db.GetCollection("docs").FindAll().GetEnumerator();
            cursor.MoveNext().Should().BeTrue();
            var ids = new System.Collections.Generic.List<int> { cursor.Current["n"].AsInt32 };
            db.GetCollection("docs").DeleteMany("_id <= 450").Should().Be(450);
            while (cursor.MoveNext()) ids.Add(cursor.Current["n"].AsInt32);
            ids.Should().BeEquivalentTo(Enumerable.Range(1, 900));
        }

        internal static LiteDatabase Open(string filename, Collation collation) => new LiteDatabase(new LiteEngine(
            new EngineSettings { Filename = filename, Collation = collation, TransactionPageLimit = 4 }));

        internal static BsonValue Id(int n, int kind) => kind == 0 ? new BsonValue(n) : kind == 1 ?
            new BsonValue("K" + n.ToString("D5")) : new BsonDocument { ["part"] = "K", ["n"] = n };

        internal static BsonDocument Document(int n, int kind) => new BsonDocument
            { ["_id"] = Id(n, kind), ["n"] = n, ["keep"] = true, ["payload"] = new string('x', 500) };

        private static void Seed(LiteDatabase db, int kind)
        {
            db.CheckpointSize = 0;
            db.GetCollection("docs").Insert(Enumerable.Range(1, 900).Select(n => Document(n, kind)));
            db.GetCollection("docs").EnsureIndex("n");
            db.GetCollection("untouched").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "safe" });
            db.Checkpoint();
        }

        private static void DeleteAndCompare(LiteDatabase fast, LiteDatabase old, BsonExpression predicate)
        {
            var fallback = BsonExpression.Create("(" + predicate.Source + ") AND keep = true", predicate.Parameters);
            fast.GetCollection("docs").DeleteMany(predicate).Should().Be(old.GetCollection("docs").DeleteMany(fallback));
            Bytes(fast).Should().Equal(Bytes(old));
            AssertIndexes(fast); AssertIndexes(old);
        }

        private static string[] Bytes(LiteDatabase db) => db.GetCollection("docs").FindAll()
            .OrderBy(d => d["n"].AsInt32).Select(d => Convert.ToBase64String(BsonSerializer.Serialize(d))).ToArray();

        internal static void AssertRows(LiteDatabase db, System.Collections.Generic.IEnumerable<int> numbers, int kind)
        {
            var expected = numbers.ToArray();
            db.GetCollection("docs").FindAll().OrderBy(d => d["n"].AsInt32)
                .Select(d => Convert.ToBase64String(BsonSerializer.Serialize(d))).Should().Equal(expected.OrderBy(n => n)
                .Select(n => Convert.ToBase64String(BsonSerializer.Serialize(Document(n, kind)))));
            foreach (var n in expected) ((object)db.GetCollection("docs").FindById(Id(n, kind))).Should().NotBeNull();
            AssertIndexes(db);
            db.GetCollection("untouched").FindById(1)["value"].AsString.Should().Be("safe");
        }

        internal static void AssertIndexes(LiteDatabase db)
        {
            var scan = db.GetCollection("docs").FindAll().Select(d => d["n"].AsInt32).OrderBy(n => n).ToArray();
            db.GetCollection("docs").Find("n >= 0").Select(d => d["n"].AsInt32).OrderBy(n => n).Should().Equal(scan);
            db.GetCollection("docs").Find("_id > MINVALUE()").Select(d => d["n"].AsInt32).OrderBy(n => n).Should().Equal(scan);
        }
    }
}
