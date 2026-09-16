using System;
using System.IO;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Constants;
using static LiteDB.Tests.Engine.WalIdentityCrash_Tests;

namespace LiteDB.Tests.Engine
{
    public class WalIdentityCompleted_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_completed_WAL_with_later_appended_commits_is_preserved(bool readOnly)
        {
            using var data = new MemoryStream();
            using var log = new MemoryStream();
            byte[] completedLog;
            byte[] completedData;
            byte[] laterLog;
            using (var db = Open(data, log))
            {
                db.CheckpointSize = 0;
                db.GetCollection("rows").Insert(Row(1));
                completedLog = log.ToArray();
                db.Checkpoint();
                completedData = data.ToArray();
                db.GetCollection("rows").Insert(Row(2));
                laterLog = log.ToArray();
            }
            // An older engine can append after the preserved predecessor prefix
            // without understanding generations. Copy complete committed frames
            // from the later WAL, omitting its modern binding prefix.
            using var appendedLog = Copy(completedLog);
            appendedLog.Position = appendedLog.Length;
            appendedLog.Write(laterLog, PAGE_SIZE, laterLog.Length - PAGE_SIZE);
            var before = appendedLog.ToArray();
            using var restoredData = Copy(completedData);
            Action open = () => { using var db = Open(restoredData, appendedLog, readOnly: readOnly); };
            open.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.INVALID_WAL);
            restoredData.ToArray().Should().Equal(completedData);
            appendedLog.ToArray().Should().Equal(before);

            using var reader = new FileReaderV8(new EngineSettings { DataStream = restoredData, LogStream = appendedLog },
                new System.Collections.Generic.List<FileReaderError>());
            Action recovery = () => reader.Open();
            recovery.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.INVALID_WAL);
            restoredData.ToArray().Should().Equal(completedData);
            appendedLog.ToArray().Should().Equal(before);
        }
    }
}
