using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2812Recovery_Tests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void Requested_recovery_rebuilds_order_or_preserves_original_on_unique_conflict(bool conflict, bool stamped)
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, Collation = Collation.Binary }))
            {
                foreach (var key in conflict ? new[] { "a", "A" } : new[] { "Z", "a" })
                    db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = key });
            }
            var bytes = File.ReadAllBytes(file.Filename);
            if (!stamped) Array.Clear(bytes, EnginePragmas.P_COLLATION_STAMP, 4);
            Array.Copy(BitConverter.GetBytes((int)CompareOptions.IgnoreCase), 0, bytes, EnginePragmas.P_COLLATION_SORT, 4);
            bytes[HeaderPage.P_INVALID_DATAFILE_STATE] = 1;
            File.WriteAllBytes(file.Filename, bytes);
            var settings = new ConnectionString { Filename = file.Filename, AutoRebuild = true, ReadOnly = true };
            if (conflict)
            {
                Action open = () => { using var db = new LiteDatabase(settings); };
                open.Should().Throw<LiteException>().WithMessage("*duplicate key*");
                File.ReadAllBytes(file.Filename).Should().Equal(bytes);
            }
            else
            {
                using var db = new LiteDatabase(settings);
                db.GetCollection("rows").Count().Should().Be(2);
                Assert.NotNull(db.GetCollection("rows").FindById("Z"));
                Assert.NotNull(db.GetCollection("rows").FindById("a"));
            }
        }
        [Fact]
        public void Later_structural_corruption_keeps_its_recovery_diagnostic_before_collation_mismatch()
        {
            using var file = new TempFile();
            using (var engine = new LiteEngine(new EngineSettings { Filename = file.Filename, Collation = Collation.Binary }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.GetCollection("before").Insert(new BsonDocument { ["_id"] = "Z" });
                db.GetCollection("before").Insert(new BsonDocument { ["_id"] = "a" });
                db.GetCollection("loop").Insert(new BsonDocument { ["_id"] = 1 });
                var monitor = (TransactionMonitor)typeof(LiteEngine).GetField("_monitor", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(engine);
                var transaction = monitor.GetTransaction(true, false, out _);
                var snapshot = transaction.CreateSnapshot(LockMode.Write, "loop", false);
                var index = snapshot.CollectionPage.GetCollectionIndexes().Single();
                var service = new IndexService(snapshot, Collation.Binary, uint.MaxValue);
                var node = service.FindAll(index, Query.Ascending).First();
                node.SetNext(0, node.Position);
                transaction.Commit();
                monitor.ReleaseTransaction(transaction);
            }
            var bytes = File.ReadAllBytes(file.Filename);
            Array.Clear(bytes, EnginePragmas.P_COLLATION_STAMP, 4);
            Array.Copy(BitConverter.GetBytes((int)CompareOptions.IgnoreCase), 0, bytes, EnginePragmas.P_COLLATION_SORT, 4);
            File.WriteAllBytes(file.Filename, bytes);
            var settings = new ConnectionString { Filename = file.Filename, AutoRebuild = true };
            Action open = () => { using var db = new LiteDatabase(settings); };
            open.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(999);
            using var recovered = new LiteDatabase(settings);
            recovered.GetCollection("before").Count().Should().Be(2);
            recovered.GetCollection("loop").Count().Should().Be(1);
        }
    }
}
