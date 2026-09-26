#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Internals
{
    public class SharedWriterResumeProcess_Tests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedb-resume-process-" + Guid.NewGuid().ToString("N"));
        private bool _passed;
        private string Filename => Path.Combine(_directory, "test.db");
        public SharedWriterResumeProcess_Tests() => Directory.CreateDirectory(_directory);

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public async Task Native_append_only_peer_resumes_and_preserves_every_acknowledged_document(string password)
        {
            await MvccProcess.Run("seed", Filename, password);
            using (var engine = new SharedEngine(new EngineSettings { Filename = Filename, Password = password }))
            using (var writer = new LiteDatabase(engine))
            {
                Prime(writer, engine);
                var before = engine.WriterResumeCount;
                using (var peer = new MvccProcess("resume-appending-peer", Filename, password))
                {
                    await peer.Expect("ready");
                    Preserve("before-resume");
                    writer.GetCollection("docs").Update(Row(63, 8));
                    engine.WriterResumeCount.Should().Be(before + 1, "the other process only appended complete commits");
                    Verify(writer, 7, 8);
                    await peer.Finish(true);
                    await peer.Expect("done");
                }
            }
            VerifyCold(password, 7, 8);
        }

        [Theory]
        [InlineData(null, "uncommitted", null)]
        [InlineData("secret", "uncommitted", null)]
        [InlineData(null, "crash-checkpoint", "data-page")]
        [InlineData("secret", "crash-checkpoint", "data-page")]
        [InlineData(null, "crash-checkpoint", "data-flushed")]
        [InlineData("secret", "crash-checkpoint", "data-flushed")]
        [InlineData(null, "crash-checkpoint", "before-reclaim")]
        [InlineData("secret", "crash-checkpoint", "before-reclaim")]
        [InlineData(null, "crash-checkpoint", "after-reclaim")]
        [InlineData("secret", "crash-checkpoint", "after-reclaim")]
        public async Task Cached_writer_rejects_abandoned_append_or_checkpoint_and_recovers(string password, string mode, string stage)
        {
            await MvccProcess.Run("seed", Filename, password);
            using (var engine = new SharedEngine(new EngineSettings { Filename = Filename, Password = password }))
            using (var writer = new LiteDatabase(engine))
            {
                Prime(writer, engine);
                var before = engine.WriterResumeCount;
                using (var peer = new MvccProcess(mode, Filename, password, stage))
                {
                    await peer.Expect("ready");
                    Preserve("before-kill");
                    await peer.Kill();
                }
                writer.GetCollection("docs").Update(Row(63, 8));
                engine.WriterResumeCount.Should().Be(before, "abandoned ownership or changed storage requires full recovery");
                Verify(writer, 2, 8);
            }
            VerifyCold(password, 2, 8);
        }

        private static void Prime(LiteDatabase writer, SharedEngine engine)
        {
            writer.CheckpointSize = 0;
            var rows = writer.GetCollection("docs");
            rows.EnsureIndex("value");
            rows.Update(Enumerable.Range(0, 64).Select(id => Row(id, 1)));
            rows.Update(Enumerable.Range(0, 64).Select(id => Row(id, 2)));
            typeof(SharedEngine).GetField("_writerResume", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(engine).Should().NotBeNull("the test must hold a reusable prefix before the peer changes storage");
        }

        private static BsonDocument Row(int id, int value) => new BsonDocument
            { ["_id"] = id, ["value"] = value, ["payload"] = new string('x', 3000) };

        private static void Verify(LiteDatabase db, int value, int last)
        {
            foreach (var name in new[] { "docs", "cold" })
            {
                var rows = db.GetCollection(name).FindAll().OrderBy(row => row["_id"].AsInt32).ToArray();
                rows.Length.Should().Be(64);
                for (var id = 0; id < rows.Length; id++)
                {
                    rows[id]["_id"].AsInt32.Should().Be(id);
                    rows[id]["value"].AsInt32.Should().Be(name == "cold" ? 0 : id == 63 ? last : value);
                    rows[id]["payload"].AsString.Should().Be(new string('x', 3000));
                }
            }
            db.GetCollection("docs").Query().Where("value = @0", last).ToArray()
                .Select(row => row["_id"].AsInt32).Should().Equal(63);
            db.GetCollection("docs").Query().Where("value = @0", value).ToArray()
                .Select(row => row["_id"].AsInt32).Should().BeEquivalentTo(Enumerable.Range(0, 63));
        }

        private void VerifyCold(string password, int value, int last)
        {
            // Validate twice, including after the first cold recovery has closed.
            for (var attempt = 0; attempt < 2; attempt++)
                using (var db = new LiteDatabase(new ConnectionString { Filename = Filename, Password = password }))
                    Verify(db, value, last);
            _passed = true;
        }

        private void Preserve(string suffix)
        {
            foreach (var path in Directory.GetFiles(_directory, "*.db"))
                using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var target = new FileStream(path + "." + suffix, FileMode.CreateNew, FileAccess.Write))
                    source.CopyTo(target);
        }

        public void Dispose()
        {
            if (_passed) Directory.Delete(_directory, true);
            else Console.Error.WriteLine("Preserved writer-resume failure: " + _directory);
        }
    }
}
#endif
