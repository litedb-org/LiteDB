using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class CompactStorageFault_Tests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("password")]
        public void Failure_at_each_wal_page_boundary_never_publishes_partial_documents_or_schemas(string password)
        {
            for (var failAt = 1; failAt <= 20; failAt++)
            {
                using var file = new TempFile();
                var settings = new EngineSettings
                {
                    Filename = file.Filename, Password = password, CompactStorage = true, TransactionPageLimit = 4
                };
                using (var engine = new LiteEngine(settings))
                using (var db = new LiteDatabase(engine, disposeOnClose: false))
                {
                    db.CheckpointSize = 0;
                    db.GetCollection("docs").Insert(CompactStorage_Tests.Document(1));
                    db.Checkpoint();
                    var writes = 0;
                    engine.SimulateDiskWriteFail = page =>
                    {
                        if (++writes == failAt) throw new IOException("Injected WAL failure");
                    };
                    try { db.GetCollection("docs").Insert(Enumerable.Range(2, 100).Select(CompactStorage_Tests.Document)); }
                    catch (IOException) { }
                }
                using (var db = new LiteDatabase(new LiteEngine(settings)))
                {
                    var docs = db.GetCollection("docs").FindAll().ToArray();
                    docs.Length.Should().BeOneOf(1, 101);
                    foreach (var doc in docs)
                        BsonSerializer.Serialize(doc).Should().Equal(BsonSerializer.Serialize(CompactStorage_Tests.Document(doc["_id"])));
                    db.Checkpoint();
                    if (password == null) File.ReadAllBytes(file.Filename)[59].Should().Be(10);
                }
            }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("password")]
        public void Failed_durable_header_flush_prevents_schema_or_document_wal_writes(string password)
        {
            using var file = new TempFile();
            using var stream = new FailingFileStream(file.Filename);
            using var log = new MemoryStream();
            var settings = new EngineSettings { DataStream = stream, LogStream = log, Password = password, CompactStorage = true };
            using (var engine = new LiteEngine(settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.GetCollection("docs").Insert(CompactStorage_Tests.Document(1));
                db.Checkpoint();
                var writesBeforeFailure = 0;
                engine.SimulateDiskWriteFail = page => { if (!stream.Failed) writesBeforeFailure++; };
                stream.Armed = true;
                Action insert = () => db.GetCollection("docs").Insert(CompactStorage_Tests.Document(2));
                insert.Should().Throw<IOException>().WithMessage("Injected header flush failure");
                writesBeforeFailure.Should().Be(0);
            }
            using var reopened = new LiteDatabase(new LiteEngine(settings));
            reopened.GetCollection("docs").FindAll().Select(d => d["_id"].AsInt32).Should().Equal(1);
        }

        [Theory]
        [InlineData(32)] // page marker
        [InlineData(34)]
        [InlineData(36)] // payload length
        [InlineData(40)] // schema ID
        [InlineData(44)] // fingerprint
        [InlineData(54)] // field definition
        public void Corrupt_catalog_fails_loudly_and_rebuild_reports_instead_of_guessing(int offset)
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, CompactStorage = true }))
                db.GetCollection("docs").Insert(Enumerable.Range(1, 10).Select(CompactStorage_Tests.Document));
            var bytes = File.ReadAllBytes(file.Filename);
            var page = Enumerable.Range(0, bytes.Length / 8192).First(i => bytes[i * 8192 + 4] == (byte)PageType.Schema);
            bytes[page * 8192 + offset] ^= 0x40;
            File.WriteAllBytes(file.Filename, bytes);
            using (var db = new LiteDatabase(file.Filename))
            {
                Action read = () => db.GetCollection("docs").FindById(2);
                read.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.CORRUPT_DOCUMENT);
            }
            var options = new RebuildOptions();
            using (var db = new LiteDatabase(file.Filename))
            {
                db.Rebuild(options);
                options.GetErrorReport().Should().NotBeEmpty();
                db.GetCollection("docs").FindAll().Select(d => d["_id"].AsInt32).Should().Equal(1);
            }
        }

        private sealed class FailingFileStream : FileStream
        {
            internal bool Armed;
            internal bool Failed;
            internal FailingFileStream(string filename) : base(filename, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite) { }
            public override void Flush(bool flushToDisk)
            {
                if (flushToDisk && Armed)
                {
                    Armed = false;
                    Failed = true;
                    throw new IOException("Injected header flush failure");
                }
                base.Flush(flushToDisk);
            }
        }
    }
}
