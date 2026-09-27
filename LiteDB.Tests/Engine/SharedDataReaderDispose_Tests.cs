using System;
using System.Threading;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// A shared reader's disposal ends one mutex recursion and one engine user. Two threads
    /// disposing the same reader at once must run that callback once: a second run would end
    /// another reader's ownership and could close the engine under it.
    /// </summary>
    public class SharedDataReaderDispose_Tests
    {
        [Fact]
        public void Concurrent_disposal_ends_the_ownership_once()
        {
            const int Readers = 20000;
            var calls = new int[1];
            var readers = new SharedDataReader[Readers];
            for (var i = 0; i < Readers; i++)
            {
                readers[i] = new SharedDataReader(new BsonDataReader(new BsonDocument(), "docs"),
                    () => Interlocked.Increment(ref calls[0]));
            }

            using var barrier = new Barrier(2);
            void DisposeAll()
            {
                foreach (var reader in readers)
                {
                    barrier.SignalAndWait();
                    reader.Dispose();
                }
            }
            var worker = new Thread(DisposeAll) { IsBackground = true };
            worker.Start();
            DisposeAll();
            worker.Join(TimeSpan.FromSeconds(60)).Should().BeTrue();

            calls[0].Should().Be(Readers, "each reader's ownership ends exactly once");
        }
    }
}
