#if DEBUG || TESTING
using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Issues;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// What a commit's caller is told about a commit that failed (LiteDB.CommitOutcome): a commit
    /// refused before it wrote anything is "NotCommitted", and a reopen shows none of it.
    /// </summary>
    [Trait("Category", "IoSafety")]
    [Collection(NativeFileSyncCollection.Name)]
    public class AcknowledgedReopen_Tests
    {
        /// <summary>A commit refused before its first frame is marked as not committed.</summary>
        [Fact]
        public void Commit_refused_before_it_writes_is_marked_not_committed()
        {
            using var file = new TempFile();
            var logName = Path.GetFullPath(FileHelper.GetLogFile(file.Filename));
            using (var setup = new LiteDatabase(file.Filename)) setup.GetCollection("rows").Insert(Row(1));
            DurableLogs.Forget(logName);
            NativeFileSync.SimulateErrno = path => Path.GetFullPath(path) == logName ? 22 : 0;
            try
            {
                using var db = new LiteDatabase(file.Filename);
                Action insert = () => db.GetCollection("rows").Insert(Row(2));
                insert.Should().Throw<IOException>().Which.Data["LiteDB.CommitOutcome"].Should().Be("NotCommitted");
            }
            finally { NativeFileSync.SimulateErrno = null; }
            using var reopened = new LiteDatabase(file.Filename);
            Ids(reopened).Should().Equal(1);
        }

        private static BsonDocument Row(int id) => new BsonDocument { ["_id"] = id };

        private static int[] Ids(LiteDatabase db) =>
            db.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x).ToArray();
    }
}
#endif
