using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2849CacheWrite_Tests
    {
        [Theory]
        [InlineData(null, false)]
        [InlineData(null, true)]
        [InlineData("secret", false)]
        [InlineData("secret", true)]
        public void Frequent_safepoints_preserve_commit_rollback_and_recovery(string password, bool rollback)
        {
            using var data = new MemoryStream();
            using var log = new MemoryStream();
            var settings = new EngineSettings
            {
                DataStream = data, LogStream = log, Password = password,
                TransactionPageLimit = 16, CacheSize = 64 * 1024
            };
            using var engine = new LiteEngine(settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            var rows = db.GetCollection("rows");
            rows.Insert(Enumerable.Range(1, 500).Select(Document));
            db.Checkpoint();
            db.BeginTrans();
            rows.Insert(Enumerable.Range(501, 500).Select(Document));
            rows.DeleteMany("_id > 20 AND _id <= 40").Should().Be(20);
            if (rollback) db.Rollback();
            else db.Commit();

            // Recover only bytes already in the streams, before close can checkpoint.
            using var recoveredData = new MemoryStream(data.ToArray());
            using var recoveredLog = new MemoryStream(log.ToArray());
            using var recoveredEngine = new LiteEngine(new EngineSettings
            {
                DataStream = recoveredData, LogStream = recoveredLog, Password = password
            });
            using var recovered = new LiteDatabase(recoveredEngine, disposeOnClose: false);
            recovered.CheckpointSize = 0;
            var expected = Enumerable.Range(1, rollback ? 500 : 1000)
                .Where(id => rollback || id <= 20 || id > 40).ToArray();
            var actual = recovered.GetCollection("rows").FindAll().ToArray();
            actual.Select(x => x["_id"].AsInt32).Should().Equal(expected);
            foreach (var row in actual)
                row["payload"].AsString.Should().Be(Document(row["_id"].AsInt32)["payload"].AsString);
        }

        private static BsonDocument Document(int id) => new BsonDocument
        {
            ["_id"] = id, ["payload"] = new string((char)('A' + id % 26), 2000)
        };
    }
}
