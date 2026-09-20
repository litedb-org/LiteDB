using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class IndexCompatibilityValidation_Tests
    {
        public static IEnumerable<object[]> Cases()
        {
            foreach (var password in new[] { null, "validation-password" })
            foreach (var readOnly in new[] { false, true })
            foreach (var wal in new[] { false, true })
            foreach (var tail in new[] { false, true })
                yield return new object[] { password, readOnly, wal, tail };
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void Incompatible_data_or_wal_is_rejected_before_tail_repair(
            string password, bool readOnly, bool wal, bool tail)
        {
            using var file = new TempFile();
            using (var db = IndexMigration_Tests.Open(file.Filename, password))
            {
                if (wal) db.CheckpointSize = 0;
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            }
            var log = FileHelper.GetLogFile(file.Filename);
            var target = wal ? log : file.Filename;
            using (var factory = new FileStreamFactory(target, password, false, false))
            using (var stream = factory.GetStream(true, wal))
            {
                var buffer = new byte[Constants.PAGE_SIZE];
                for (long offset = 0; offset < stream.Length; offset += buffer.Length)
                {
                    stream.Position = offset;
                    stream.Read(buffer, 0, buffer.Length).Should().Be(buffer.Length);
                    if (buffer[BasePage.P_PAGE_TYPE] != (byte)PageType.Header) continue;
                    buffer[EnginePragmas.P_COLLATION_STAMP] ^= 1;
                    stream.Position = offset;
                    stream.Write(buffer, 0, buffer.Length);
                }
                stream.FlushToDisk();
            }
            if (tail)
            {
                using (var stream = File.Open(file.Filename, FileMode.Append)) stream.Write(new byte[17], 0, 17);
                if (File.Exists(log))
                    using (var stream = File.Open(log, FileMode.Append)) stream.Write(new byte[17], 0, 17);
            }
            var dataBefore = File.ReadAllBytes(file.Filename);
            var logBefore = File.Exists(log) ? File.ReadAllBytes(log) : null;
            Action open = () => { using var db = IndexMigration_Tests.Open(file.Filename, password, readOnly); };
            open.Should().Throw<LiteException>().WithMessage("*ordering/collation*");
            File.ReadAllBytes(file.Filename).Should().Equal(dataBefore);
            if (logBefore != null) File.ReadAllBytes(log).Should().Equal(logBefore);
        }
    }
}
