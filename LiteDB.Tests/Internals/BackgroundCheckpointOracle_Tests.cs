using System;
using FluentAssertions;
using LiteDB.Tests;
using LiteDB.Tests.Engine;
using Xunit;

namespace LiteDB.Internals
{
    public class BackgroundCheckpointOracle_Tests
    {
        [Theory]
        [InlineData("partial")]
        [InlineData("payload")]
        [InlineData("index")]
        [InlineData("sentinel")]
        public void RecoveryOracleRejectsIncompleteOrChangedAcknowledgedState(string damage)
        {
            using var file = new TempFile();
            using var db = new LiteDatabase(file.Filename);
            BackgroundCheckpointWorkload.Seed(db);
            db.GetCollection("rows").Update(BackgroundCheckpointWorkload.Rows(1));
            BackgroundCheckpointWorkload.Verify(db, 1);
            var rows = db.GetCollection("rows");
            if (damage == "partial") rows.Update(BackgroundCheckpointWorkload.Rows(0)[5]);
            if (damage == "payload")
            {
                var row = rows.FindById(5);
                row["payload"] = "changed";
                rows.Update(row);
            }
            if (damage == "index") rows.DropIndex("value");
            if (damage == "sentinel") db.GetCollection("sentinel").DeleteAll();
            Action verify = () => BackgroundCheckpointWorkload.Verify(db, 1);
            verify.Should().Throw<InvalidOperationException>();
        }
    }
}
