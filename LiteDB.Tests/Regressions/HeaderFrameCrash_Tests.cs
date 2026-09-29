#if DEBUG || TESTING
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Issues;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// Decision 11: the first commit of an empty WAL writes the header frame (a copy of the data
    /// header) before its first page frame. Cut at the header frame's crash points, before and after
    /// its write, a power loss (each file as of its last successful sync) and a process crash (every
    /// byte handed to the operating system: after the write, a WAL holding only the header frame)
    /// both leave exactly the rows committed before the cut commit, and the database takes the next
    /// commit. Failure models: modeled-power-loss and process-death.
    /// </summary>
    [Trait("Category", "IoSafety")]
    [Collection(NativeFileSyncCollection.Name)]
    public class HeaderFrameCrash_Tests
    {
        [Theory]
        [InlineData("header-frame-before-write", null)]
        [InlineData("header-frame-after-write", null)]
        [InlineData("header-frame-before-write", "secret")]
        [InlineData("header-frame-after-write", "secret")]
        public void First_commit_of_an_empty_wal_cut_at_its_header_frame_keeps_the_committed_rows(string phase, string password)
        {
            using var file = new TempFile();
            var connection = password == null ? file.Filename : $"Filename={file.Filename};Password={password}";
            var logName = FileHelper.GetLogFile(file.Filename);
            // The close checkpoint drains the WAL: the next commit starts an empty one.
            using (var setup = new LiteDatabase(connection)) setup.GetCollection("rows").Insert(Enumerable.Range(1, 10).Select(Row));

            (byte[] Data, byte[] Log) powerLoss = default, processCrash = default;
            var hits = 0;
            using (var power = new FilePowerLossModel(file.Filename))
            {
                using (var engine = new LiteEngine(new EngineSettings { Filename = file.Filename, Password = password }))
                using (var db = new LiteDatabase(engine, disposeOnClose: false))
                {
                    engine.SimulateCrashPoint = reached =>
                    {
                        if (reached != phase) return;
                        hits++;
                        powerLoss = power.Capture();
                        processCrash = (ReadShared(file.Filename), ReadShared(logName));
                    };
                    db.GetCollection("rows").Insert(Enumerable.Range(11, 5).Select(Row));
                    engine.SimulateCrashPoint = null;
                }
                hits.Should().Be(1, "the first commit of the empty WAL writes its header frame once");
                if (phase == "header-frame-after-write" && password == null)
                    processCrash.Log.Length.Should().BeGreaterOrEqualTo(WalChecksum.FrameSize, "the header frame was handed to the operating system");

                foreach (var image in new[] { powerLoss, processCrash })
                {
                    FilePowerLossModel.Open(image, db =>
                    {
                        var rows = db.GetCollection("rows");
                        Ids(rows).Should().Equal(Enumerable.Range(1, 10), "the cut commit wrote no page frame");
                        rows.Find(Query.EQ("value", 3)).Select(x => x["_id"].AsInt32).Should().Equal(3);
                        rows.Insert(Row(100));
                        Ids(rows).Should().Equal(Enumerable.Range(1, 10).Concat(new[] { 100 }), "the image takes the next commit");
                        return 0;
                    }, password);
                }
            }
        }

        private static BsonDocument Row(int id) => new BsonDocument { ["_id"] = id, ["value"] = id, ["payload"] = new string('x', 300) };

        private static int[] Ids(ILiteCollection<BsonDocument> rows) => rows.FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x).ToArray();

        private static byte[] ReadShared(string filename) =>
            File.Exists(filename) ? SyncPowerLossModel.ReadShared(filename) : new byte[0];
    }
}
#endif
