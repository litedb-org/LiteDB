using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Tests.Issues
{
    public class Issue2891_Tests
    {
        [Fact]
        public void Collection_snapshot_survives_a_header_change_while_it_is_enumerated()
        {
            var buffer = new PageBuffer(new byte[PAGE_SIZE], 0, 0);
            var header = new HeaderPage(buffer, 0);
            header.InsertCollection("first", 1);
            header.InsertCollection("second", 2);

            using var iterator = header.GetCollections().GetEnumerator();
            iterator.MoveNext().Should().BeTrue();
            var observed = new List<string> { iterator.Current.Key };

            lock (header)
            {
                header.InsertCollection("created-concurrently", 3);
            }

            Action finishSnapshot = () =>
            {
                while (iterator.MoveNext()) observed.Add(iterator.Current.Key);
            };

            finishSnapshot.Should().NotThrow<InvalidOperationException>();
            observed.Should().BeEquivalentTo("first", "second");
        }

        [Fact]
        public void Snapshot_is_captured_at_call_time_and_survives_rename_and_delete()
        {
            var header = new HeaderPage(new PageBuffer(new byte[PAGE_SIZE], 0, 0), 0);
            header.InsertCollection("first", 1);
            header.InsertCollection("second", 2);
            var snapshot = header.GetCollections();
            lock (header)
            {
                header.RenameCollection("first", "renamed");
                header.DeleteCollection("second");
            }
            snapshot.Should().BeEquivalentTo(new[]
            {
                new KeyValuePair<string, uint>("first", 1),
                new KeyValuePair<string, uint>("second", 2)
            });
            header.GetCollections().Single().Key.Should().Be("renamed");
        }

        [Fact]
        public async Task Concurrent_collection_creation_and_listing_complete_without_errors()
        {
            using var file = new TempFile();
            using var db = new LiteDatabase(file.Filename);
            using var start = new ManualResetEventSlim();
            var writers = Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
            {
                start.Wait();
                for (var i = worker; i < 200; i += 4)
                    db.GetCollection("c" + i).Insert(new BsonDocument { ["_id"] = i });
            })).ToArray();
            var readers = Enumerable.Range(0, 2).Select(worker => Task.Run(() =>
            {
                start.Wait();
                for (var i = 0; i < 100; i++)
                {
                    db.GetCollectionNames().ToArray();
                    using var cols = db.Execute("SELECT name FROM $cols");
                    while (cols.Read()) { }
                }
            })).ToArray();
            start.Set();
            await Task.WhenAll(writers.Concat(readers));
            db.GetCollectionNames().Should().HaveCount(200);
            for (var i = 0; i < 200; i++) Assert.NotNull(db.GetCollection("c" + i).FindById(i));
        }
    }
}
