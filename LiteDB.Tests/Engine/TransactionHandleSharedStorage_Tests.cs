using System;
using System.IO;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class TransactionHandleSharedStorage_Tests
    {
        [Theory]
        [InlineData(":memory:")] [InlineData(":temp:")]
        public void Shared_private_storage_cannot_claim_persistent_handle_commits(string filename)
        {
            Assert.Throws<NotSupportedException>(() =>
            {
                using var db = new LiteDatabase(new SharedEngine(new EngineSettings { Filename = filename }));
                using var tx = db.BeginTransaction();
            });
        }

        [Fact]
        public void Shared_caller_stream_refusal_precedes_storage_mutation()
        {
            using var data = new MemoryStream();
            using var log = new MemoryStream();
            using var temp = new MemoryStream();
            using (var engine = new SharedEngine(new EngineSettings { Filename = "stream-handle", DataStream = data, LogStream = log, TempStream = temp }))
            using (var db = new LiteDatabase(engine))
            {
                Assert.Throws<NotSupportedException>(() => db.BeginTransaction());
                Assert.Equal(0, db.TransactionHandles.ActiveCount);
                Assert.Equal(0, data.Length); Assert.Equal(0, log.Length); Assert.Equal(0, temp.Length);
            }
            Assert.True(data.CanRead); Assert.True(log.CanRead); Assert.True(temp.CanRead);
        }
    }
}
