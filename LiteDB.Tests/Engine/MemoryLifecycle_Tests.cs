using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class MemoryLifecycle_Tests
    {
        [Theory]
        [InlineData("SELECT FIRST(*) FROM docs ORDER BY sort")]
        [InlineData("SELECT * FROM docs ORDER BY sort")]
        public void Aggregate_Releases_Sort_Storage_When_Cursor_Closes(string sql)
        {
            using var temporary = new MemoryStream();
            using var engine = new LiteEngine(new EngineSettings { Filename = ":memory:", TempStream = temporary });
            using var database = new LiteDatabase(engine, disposeOnClose: false);
            database.GetCollection("docs").InsertBulk(Enumerable.Range(0, 12000).Select(i => new BsonDocument
            {
                ["_id"] = i,
                ["sort"] = i.ToString("D6") + new string('x', 100)
            }));

            long allocated = 0;
            for (var i = 0; i < 3; i++)
            {
                // Close early as well as exercising the scalar FIRST result.
                using (var reader = database.Execute(sql)) reader.Read().Should().BeTrue();
                if (i == 0) allocated = temporary.Length;
                temporary.Length.Should().Be(allocated);
            }

            allocated.Should().BeGreaterThan(0);
        }

        [Fact]
        public async Task Database_Diagnostics_Tolerate_Concurrent_Transaction_Churn()
        {
            using var database = new LiteDatabase(":memory:");
            var collection = database.GetCollection("docs");
            collection.Insert(new BsonDocument { ["_id"] = 1 });
            using var start = new ManualResetEventSlim();
            var readers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                start.Wait();
                for (var i = 0; i < 1000; i++) (collection.FindById(1) != null).Should().BeTrue();
            })).ToArray();

            start.Set();
            for (var i = 0; i < 1000; i++)
            {
                var info = database.Execute("SELECT $ FROM $database").Single()["transactions"];
                info["open"].AsInt32.Should().Be(info["transactionPages"].AsArray.Count);
                info["open"].AsInt32.Should().BeInRange(0, 100);
            }

            await Task.WhenAll(readers);
        }

        [Fact]
        public void Transaction_Registry_Enforces_Limit_And_Reuses_Released_Slots()
        {
            using var engine = new LiteEngine(new EngineSettings { Filename = ":memory:" });
            var monitor = engine.GetMonitor();
            var transactions = Enumerable.Range(0, 100)
                .Select(index => monitor.GetTransaction(true, true, out _)).ToArray();

            Action overflow = () => monitor.GetTransaction(true, true, out _);
            overflow.Should().Throw<LiteException>().WithMessage("*Maximum number*");
            monitor.ReleaseTransaction(transactions[50]);
            transactions[50] = monitor.GetTransaction(true, true, out _);
            monitor.Transactions.Count.Should().Be(100);
            foreach (var transaction in transactions) monitor.ReleaseTransaction(transaction);
            monitor.Transactions.Should().BeEmpty();
        }
    }
}
