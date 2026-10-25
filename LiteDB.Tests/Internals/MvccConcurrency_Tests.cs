using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace LiteDB.Internals
{
    public class MvccConcurrency_Tests
    {
        [Fact]
        public async Task ReadersWritersAndCheckpointsCanContinuouslyRace()
        {
            using var test = new WalTestDatabase(null);
            test.Seed("first");
            test.Seed("second");
            using var start = new ManualResetEventSlim();
            var workers = new[]
            {
                Task.Run(() => Write("first")),
                Task.Run(() => Write("second")),
                Task.Run(() => Read("first")),
                Task.Run(() => Read("second")),
                Task.Run(() =>
                {
                    start.Wait();
                    for (var i = 0; i < 200; i++) test.Engine.Checkpoint();
                })
            };
            start.Set();
            await Task.WhenAll(workers);
            test.Engine.GetWalIndex().SnapshotCount.Should().Be(0);
            test.Engine.Checkpoint();
            test.Recover("first", true).Should().OnlyContain(doc => doc["value"].AsInt32 == 100);
            test.Recover("second", true).Should().OnlyContain(doc => doc["value"].AsInt32 == 100);

            void Write(string collection)
            {
                start.Wait();
                for (var i = 1; i <= 100; i++) test.Update(collection, i);
            }

            void Read(string collection)
            {
                start.Wait();
                for (var i = 0; i < 200; i++)
                {
                    using var reader = test.Engine.Query(collection, new Query());
                    var value = reader.Current["value"].AsInt32;
                    var count = 0;
                    while (reader.Read())
                    {
                        reader.Current["value"].AsInt32.Should().Be(value);
                        count++;
                        Thread.Yield();
                    }
                    count.Should().Be(WalTestDatabase.DocumentCount);
                }
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void DeletingAndReusingDataPagesDoesNotChangeOldSnapshot(bool dropCollection)
        {
            using var test = new WalTestDatabase(null);
            test.Seed("docs");
            using var reader = test.Engine.Query("docs", new Query());
            MvccCheckpoint_Tests.RunThread(() =>
            {
                if (dropCollection) test.Database.DropCollection("docs");
                else test.Database.GetCollection("docs").DeleteAll();
                test.Engine.Checkpoint();
                test.Seed("docs");
                test.Update("docs", 1);
                test.Engine.Checkpoint();
            });
            var values = 0;
            while (reader.Read())
            {
                reader.Current["value"].AsInt32.Should().Be(0);
                values++;
            }
            values.Should().Be(WalTestDatabase.DocumentCount);
            reader.Dispose();
            test.Engine.Checkpoint();
            test.Recover("docs", true).Should().OnlyContain(doc => doc["value"].AsInt32 == 1);
        }
    }
}
