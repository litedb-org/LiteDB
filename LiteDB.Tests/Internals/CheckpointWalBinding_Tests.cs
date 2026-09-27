using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Internals
{
    public class CheckpointWalBinding_Tests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void WalChangesDuringJournalBinding_AreRejectedBeforeDataWrites(string password)
        {
            using var data = new MemoryStream();
            using var log = new ChangingLog();
            using var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            db.GetCollection("docs").Insert(Documents(0));
            db.Checkpoint();
            var original = data.ToArray();
            db.GetCollection("docs").Update(Documents(1));
            var length = log.Length;
            log.Start = password == null ? 0 : PAGE_SIZE;
            log.Armed = true;
            Action checkpoint = () => db.Checkpoint();
            checkpoint.Should().Throw<PageChecksumException>();
            log.Changed.Should().BeTrue();
            data.ToArray().Should().Equal(original, "journal binding must validate the bytes it seals, not rely on an earlier read");
            log.Length.Should().Be(length, "no recovery journal may seal damaged redo");
        }

        [Theory]
        [InlineData(null, false)]
        [InlineData("secret", false)]
        [InlineData(null, true)]
        [InlineData("secret", true)]
        public void WalDamageAfterDataCopyStarts_CannotExposeAPartialTransaction(string password, bool readOnly)
        {
            using var data = new ObservedWrites();
            using var log = new MemoryStream();
            using var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            var docs = db.GetCollection("docs");
            docs.Insert(Documents(0));
            docs.EnsureIndex("value");
            db.Checkpoint();
            var original = data.ToArray();
            docs.Update(Documents(1));
            var preamble = password == null ? 0 : PAGE_SIZE;
            var lastFrame = ((log.Length - preamble) / WalChecksum.FrameSize - 1) * WalChecksum.FrameSize + preamble;
            data.AfterWrite = () =>
            {
                // Preflight succeeded and a data page has already been copied.
                // Damage durable redo afterwards, beyond the power-loss model.
                var position = log.Position;
                log.Position = lastFrame + 400;
                var value = log.ReadByte();
                log.Position--;
                log.WriteByte((byte)(value ^ 1));
                log.Position = position;
            };
            Action checkpoint = () => db.Checkpoint();
            checkpoint.Should().Throw<PageChecksumException>();
            data.ToArray().SequenceEqual(original).Should().BeFalse("at least one data page must already have changed");
            var beforeData = data.ToArray();
            var beforeLog = log.ToArray();
            using var recoveryData = ChecksumTestFiles.Copy(beforeData);
            using var recoveryLog = ChecksumTestFiles.Copy(beforeLog);
            var settings = new EngineSettings { DataStream = recoveryData, LogStream = recoveryLog, Password = password, ReadOnly = readOnly };
            Action open = () => { using var recovered = new LiteEngine(settings); };
            open.Should().Throw<PageChecksumException>("checkpoint redo cannot be discarded after data overwrites began");
            recoveryData.ToArray().Should().Equal(beforeData);
            recoveryLog.ToArray().Should().Equal(beforeLog);
            open.Should().Throw<PageChecksumException>("a failed open must preserve the evidence for subsequent opens");
            var errors = new List<FileReaderError>();
            using var reader = new FileReaderV8(settings, errors);
            reader.Open();
            errors.Should().Contain(x => x.Exception is PageChecksumException);
            recoveryData.ToArray().Should().Equal(beforeData);
            recoveryLog.ToArray().Should().Equal(beforeLog);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void PublishedGeneration_DoesNotRequireTheOldCheckpointWal(string password)
        {
            using var data = new ObservedWrites();
            using var log = new MemoryStream();
            using var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            var docs = db.GetCollection("docs");
            docs.Insert(Documents(0));
            docs.EnsureIndex("value");
            db.Checkpoint();
            docs.Update(Documents(1));
            byte[] journal = null;
            data.AfterWrite = () => journal = log.ToArray();
            db.Checkpoint();
            journal[(password == null ? 0 : PAGE_SIZE) + 400] ^= 1;
            using var recoveryData = ChecksumTestFiles.Copy(data.ToArray());
            using var recoveryLog = ChecksumTestFiles.Copy(journal);
            var settings = new EngineSettings { DataStream = recoveryData, LogStream = recoveryLog, Password = password };
            using (var recoveredEngine = new LiteEngine(settings))
            using (var recovered = new LiteDatabase(recoveredEngine, disposeOnClose: false))
            {
                recovered.GetCollection("docs").FindAll().Should().BeEquivalentTo(Documents(1));
                recovered.GetCollection("docs").Find(Query.EQ("value", 1)).Should().HaveCount(16);
                recovered.Checkpoint();
            }
            using var reopenedEngine = new LiteEngine(settings);
            using var reopened = new LiteDatabase(reopenedEngine, disposeOnClose: false);
            reopened.GetCollection("docs").FindAll().Should().BeEquivalentTo(Documents(1));
        }

        private static BsonDocument[] Documents(int value) => Enumerable.Range(0, 16).Select(id => new BsonDocument
        {
            ["_id"] = id, ["value"] = value, ["payload"] = new string('x', 1500)
        }).ToArray();

        private sealed class ObservedWrites : MemoryStream
        {
            internal Action AfterWrite;

            public override void Write(byte[] buffer, int offset, int count)
            {
                base.Write(buffer, offset, count);
                if (count != PAGE_SIZE) return;
                var action = AfterWrite;
                AfterWrite = null;
                action?.Invoke();
            }
        }

        private sealed class ChangingLog : MemoryStream
        {
            internal bool Armed;
            internal bool Changed;
            internal long Start;
            private int _passes;

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (Armed && Position == Start && ++_passes == 2)
                {
                    var lastFrame = ((Length - Start) / WalChecksum.FrameSize - 1) * WalChecksum.FrameSize + Start;
                    GetBuffer()[lastFrame + 400] ^= 1;
                    Changed = true;
                }
                return base.Read(buffer, offset, count);
            }
        }
    }
}
