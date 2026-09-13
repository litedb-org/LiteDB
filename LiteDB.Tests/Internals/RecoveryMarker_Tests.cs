using System;
using System.IO;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Internals
{
    public class RecoveryMarker_Tests
    {
        [Theory]
        [InlineData(null, false)]
        [InlineData("secret", false)]
        [InlineData(null, true)]
        [InlineData("secret", true)]
        public void InvalidState_PersistsMarkerBeforeClosingDataWriter(string password, bool fileBacked)
        {
            using var file = new TempFile();
            using var data = new MemoryStream();
            var settings = new EngineSettings { Password = password };
            if (fileBacked) settings.Filename = file.Filename;
            else settings.DataStream = data;
            using var engine = new LiteEngine(settings);
            using var database = new LiteDatabase(engine, disposeOnClose: false);
            var collection = database.GetCollection("docs");
            collection.Insert(new BsonDocument { ["_id"] = 1 });
            database.Checkpoint();
            engine.SimulateDiskReadFail = _ => throw LiteException.InvalidDatafileState("injected invalid page");

            Action read = () => collection.FindById(1);
            read.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.INVALID_DATAFILE_STATE);

            // A fresh factory verifies persistence without reviving a disposed one.
            using var factory = settings.CreateDataFactory();
            using var reader = factory.GetStream(true, false);
            var header = new byte[PAGE_SIZE];
            reader.Read(header, 0, header.Length).Should().Be(PAGE_SIZE);
            header[HeaderPage.P_INVALID_DATAFILE_STATE].Should().Be(1);
            data.CanRead.Should().BeTrue();
        }

        [Fact]
        public void MarkerWriteFailure_StillClosesOwnedResources()
        {
            using var data = new MarkerFailureStream();
            using var engine = new LiteEngine(new EngineSettings { DataStream = data });
            using var database = new LiteDatabase(engine, disposeOnClose: false);
            database.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
            database.Checkpoint();
            var monitor = engine.GetMonitor();
            var transaction = monitor.GetTransaction(true, true, out _);
            var cache = transaction.CreateSnapshot(LockMode.Read, "docs", false).CollectionPage.Buffer.Cache;
            monitor.ReleaseTransaction(transaction);
            data.FailWrites = true;

            var errors = engine.Close(LiteException.InvalidDatafileState("injected invalid page"));

            errors.Should().Contain(error => error is IOException && error.Message == "injected marker failure");
            engine.GetMonitor().Transactions.Should().BeEmpty();
            cache.TotalPages.Should().Be(0);
            data.CanRead.Should().BeTrue("the data stream belongs to the caller");
        }

        private sealed class MarkerFailureStream : MemoryStream
        {
            public bool FailWrites { get; set; }

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (this.FailWrites) throw new IOException("injected marker failure");
                base.Write(buffer, offset, count);
            }
        }
    }
}
