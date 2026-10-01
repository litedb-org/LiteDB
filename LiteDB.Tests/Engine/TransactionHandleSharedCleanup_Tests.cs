using System;
using System.IO;
using System.Reflection;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class TransactionHandleSharedCleanup_Tests
    {
        [Theory]
        [InlineData("DataStream")]
        [InlineData("LogStream")]
        [InlineData("TempStream")]
        public void Shared_caller_stream_handle_rejects_before_stream_access(string property)
        {
            using var file = new TempFile();
            using var stream = new MemoryStream();
            var settings = new EngineSettings { Filename = file };
            typeof(EngineSettings).GetProperty(property).SetValue(settings, stream);
            using var engine = new SharedEngine(settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            Assert.Throws<NotSupportedException>(() => db.BeginTransaction());
            Assert.Equal(0, stream.Length);
            Assert.True(stream.CanWrite);
        }
    }
}
