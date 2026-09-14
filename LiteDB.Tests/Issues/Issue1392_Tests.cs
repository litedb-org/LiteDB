using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1392_Tests
    {
        private const int SeedCount = 2200;
        private const int ReaderCount = 8;
        private const int ReadsPerReader = 600;
        private const int WriterCount = 2;
        private const int SlotsPerWriter = 80;
        private const int FinalRound = 4;

        [Fact]
        public Task Concurrent_index_reads_and_upserts_preserve_the_ledger_during_cache_recycling()
        {
            return VerifyConcurrentIndexReadsAndUpserts(requireCacheRecycling: true);
        }

        // Historical releases predate the configurable cache. Their runner still
        // checks every query/upsert and persisted row, and reports that limitation.
        public async Task VerifyConcurrentIndexReadsAndUpserts(bool requireCacheRecycling)
        {
            using var file = new TempFile();
            var connection = new ConnectionString(file.Filename);
            if (requireCacheRecycling)
            {
                var cacheSize = typeof(ConnectionString).GetProperty("CacheSize");
                cacheSize.Should().NotBeNull("current-source coverage must retain the cache-pressure control");
                cacheSize.SetValue(connection, 64L * 1024);
            }
            var seed = Enumerable.Range(0, SeedCount).Select(CreateSeed).ToArray();
            var errors = new ConcurrentQueue<Exception>();
            long completedReads = 0;
            long completedUpserts = 0;

            using (var db = new LiteDatabase(connection))
            {
                var rows = db.GetCollection("records");
                rows.InsertBulk(seed.Select(ToDocument), 200).Should().Be(SeedCount);
                rows.EnsureIndex("lookup", "$.lookup", true).Should().BeTrue();
                db.Checkpoint();

                using var start = new ManualResetEventSlim();
                using var ready = new CountdownEvent(ReaderCount + WriterCount);
                var workers = new List<Task>();

                for (var worker = 0; worker < ReaderCount; worker++)
                {
                    var reader = worker;
                    workers.Add(StartWorker(ready, start, errors, () =>
                    {
                        for (var iteration = 0; iteration < ReadsPerReader; iteration++)
                        {
                            var expected = seed[(reader * 997 + iteration * 37) % seed.Length];
                            AssertRow(rows.FindById(expected.Id), expected);
                            AssertRow(rows.FindOne(Query.EQ("lookup", expected.Lookup)), expected);

                            if ((iteration & 15) == 0)
                            {
                                rows.Exists(Query.EQ("_id", expected.Id)).Should().BeTrue();
                                rows.Exists(Query.EQ("_id", "missing-" + expected.Id)).Should().BeFalse();
                            }

                            Interlocked.Increment(ref completedReads);
                        }
                    }));
                }

                for (var worker = 0; worker < WriterCount; worker++)
                {
                    var writer = worker;
                    workers.Add(StartWorker(ready, start, errors, () =>
                    {
                        for (var round = 1; round <= FinalRound; round++)
                        {
                            for (var slot = 0; slot < SlotsPerWriter; slot++)
                            {
                                var expected = CreateMutable(writer, slot, round);
                                rows.Upsert(ToDocument(expected)).Should().Be(round == 1,
                                    "Upsert returns true only when it inserts a new id");
                                AssertRow(rows.FindById(expected.Id), expected);
                                Interlocked.Increment(ref completedUpserts);
                            }
                        }
                    }));
                }

                ready.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("all bounded workers must start");
                start.Set();
                var allWorkers = Task.WhenAll(workers);
                var completed = await Task.WhenAny(allWorkers, Task.Delay(TimeSpan.FromSeconds(30)));
                completed.Should().BeSameAs(allWorkers, "the workload must not hang");
                await allWorkers;

                errors.Should().BeEmpty();
                completedReads.Should().Be(ReaderCount * ReadsPerReader);
                completedUpserts.Should().Be(WriterCount * SlotsPerWriter * FinalRound);

                if (requireCacheRecycling)
                {
                    var database = db.Execute("SELECT $ FROM $database").Single().AsDocument;
                    var cache = database["cache"].AsDocument;
                    database["dataFileSize"].AsInt32.Should().BeGreaterThan(
                        cache["limitPagesRounded"].AsInt32 * 8192,
                        "the database must exceed the cache working set");
                    cache["evictedPages"].AsInt64.Should().BeGreaterThan(0,
                        "the regression must actually recycle cache frames");
                    cache["pinnedPages"].AsInt32.Should().Be(0);
                    cache["lostFrames"].AsInt64.Should().Be(0);
                }
            }

            var expectedRows = seed
                .Concat(Enumerable.Range(0, WriterCount).SelectMany(writer =>
                    Enumerable.Range(0, SlotsPerWriter).Select(slot => CreateMutable(writer, slot, FinalRound))))
                .ToDictionary(x => x.Id, StringComparer.Ordinal);

            using (var reopened = new LiteDatabase(connection))
            {
                var rows = reopened.GetCollection("records");
                var scan = rows.FindAll().ToDictionary(x => x["_id"].AsString, StringComparer.Ordinal);
                scan.Keys.Should().BeEquivalentTo(expectedRows.Keys);
                foreach (var pair in expectedRows) AssertRow(scan[pair.Key], pair.Value);

                rows.Find(Query.All("lookup"))
                    .Select(x => x["_id"].AsString)
                    .Should().Equal(expectedRows.Values.OrderBy(x => x.Lookup).Select(x => x.Id));
            }
        }

        private static Task StartWorker(
            CountdownEvent ready,
            ManualResetEventSlim start,
            ConcurrentQueue<Exception> errors,
            Action action)
        {
            return Task.Factory.StartNew(() =>
            {
                ready.Signal();
                try
                {
                    start.Wait();
                    action();
                }
                catch (Exception ex)
                {
                    errors.Enqueue(ex);
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        private static ExpectedRow CreateSeed(int value)
        {
            return Create(
                "seed-" + value.ToString("D6"),
                "lookup-" + (SeedCount - value).ToString("D6"),
                value,
                new string((char)('A' + value % 26), 700 + value % 17 * 31));
        }

        private static ExpectedRow CreateMutable(int writer, int slot, int round)
        {
            var payloadSizes = new[] { 0, 128, 4096, 9000, 512 };
            var value = 100000 + writer * SlotsPerWriter + slot;
            return Create(
                "writer-" + writer + "-" + slot.ToString("D3"),
                "mutable-" + writer + "-" + slot.ToString("D3") + "-round-" + round,
                value,
                new string((char)('a' + (writer + slot + round) % 26), payloadSizes[round]));
        }

        private static ExpectedRow Create(string id, string lookup, int value, string payload)
        {
            unchecked
            {
                var checksum = 17;
                foreach (var character in id) checksum = checksum * 31 + character;
                foreach (var character in lookup) checksum = checksum * 31 + character;
                foreach (var character in payload) checksum = checksum * 31 + character;
                checksum = checksum * 31 + value;
                return new ExpectedRow(id, lookup, value, payload, checksum);
            }
        }

        private static BsonDocument ToDocument(ExpectedRow expected)
        {
            return new BsonDocument
            {
                ["_id"] = expected.Id,
                ["lookup"] = expected.Lookup,
                ["value"] = expected.Value,
                ["payload"] = expected.Payload,
                ["checksum"] = expected.Checksum
            };
        }

        private static void AssertRow(BsonDocument actual, ExpectedRow expected)
        {
            Assert.NotNull(actual);
            actual["_id"].AsString.Should().Be(expected.Id);
            actual["lookup"].AsString.Should().Be(expected.Lookup);
            actual["value"].AsInt32.Should().Be(expected.Value);
            actual["payload"].AsString.Should().Be(expected.Payload);
            actual["checksum"].AsInt32.Should().Be(expected.Checksum);
        }

        private sealed class ExpectedRow
        {
            public ExpectedRow(string id, string lookup, int value, string payload, int checksum)
            {
                this.Id = id;
                this.Lookup = lookup;
                this.Value = value;
                this.Payload = payload;
                this.Checksum = checksum;
            }

            public string Id { get; }
            public string Lookup { get; }
            public int Value { get; }
            public string Payload { get; }
            public int Checksum { get; }
        }
    }
}
