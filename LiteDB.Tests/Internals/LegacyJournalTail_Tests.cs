using System;
using System.IO;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Internals
{
    public class LegacyJournalTail_Tests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void LegacyCommitsAfterAConversionFooter_AreReplayedBeforeConversion(string password)
        {
            using var source = new WalTestDatabase(password);
            source.Seed("docs");
            source.Database.Checkpoint();
            using var data = new HeaderFailure { HeaderPosition = password == null ? 0 : PAGE_SIZE };
            var original = source.Data.ToArray();
            data.Write(original, 0, original.Length);
            using var log = new MemoryStream();
            ChecksumTestFiles.MakeLegacy(data, log, password);
            data.Armed = true;
            Action convert = () => { using var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password }); };
            convert.Should().Throw<IOException>().WithMessage("stop conversion");

            // A legacy engine can still append ordinary transactions while the
            // primary header is v8. Its acknowledged tail must not be mistaken
            // for an unfinished conversion backup and silently discarded.
            var header = new BufferSlice(new byte[PAGE_SIZE], 0, PAGE_SIZE);
            using (var factory = new StreamFactory(data, password))
            using (var stream = factory.GetStream(false, false)) stream.ReadRequired(header.Array, 0, PAGE_SIZE);
            header.Write(2u, BasePage.P_TRANSACTION_ID);
            header.Write(true, BasePage.P_IS_CONFIRMED);
            header.Write(123, EnginePragmas.P_USER_VERSION);
            using (var factory = new StreamFactory(log, password))
            using (var stream = factory.GetStream(true, false))
            {
                stream.Position = stream.Length;
                stream.Write(header.Array, 0, PAGE_SIZE);
                stream.FlushToDisk();
            }
            var dataBytes = data.ToArray();
            var logBytes = log.ToArray();
            foreach (var readOnly in new[] { true, false })
            {
                using var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password, ReadOnly = readOnly });
                using var db = new LiteDatabase(engine, disposeOnClose: false);
                db.UserVersion.Should().Be(123);
                db.GetCollection("docs").Count().Should().Be(WalTestDatabase.DocumentCount);
                if (readOnly)
                {
                    data.ToArray().Should().Equal(dataBytes);
                    log.ToArray().Should().Equal(logBytes);
                }
                else db.Checkpoint();
            }
            using var reopenedEngine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password });
            using var reopened = new LiteDatabase(reopenedEngine, disposeOnClose: false);
            reopened.UserVersion.Should().Be(123);
        }

        private sealed class HeaderFailure : MemoryStream
        {
            internal bool Armed;
            internal long HeaderPosition;
            public override void Write(byte[] buffer, int offset, int count)
            {
                if (Armed && Position == HeaderPosition && count == PAGE_SIZE)
                {
                    Armed = false;
                    throw new IOException("stop conversion");
                }
                base.Write(buffer, offset, count);
            }
        }
    }
}
