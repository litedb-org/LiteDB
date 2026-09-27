#if !NETFRAMEWORK
#pragma warning disable LITEDB_EXPERIMENTAL_COORDINATOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Internals
{
    /// <summary>Experimental coordinator mode across real processes.</summary>
    public class CoordinatorProcess_Tests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedb-coord-" + Guid.NewGuid().ToString("N"));
        private string Filename => Path.Combine(_directory, "test.db");

        public CoordinatorProcess_Tests() => Directory.CreateDirectory(_directory);

        [Fact]
        public async Task Processes_write_concurrently_through_a_coordinator_process()
        {
            using var host = new MvccProcess("coord-host", Filename, null);
            await host.Expect("ready");
            using (var a = new MvccProcess("coord-writer", Filename, null, "a:150"))
            using (var b = new MvccProcess("coord-writer", Filename, null, "b:150"))
            {
                var results = await Task.WhenAll(ReadToDone(a), ReadToDone(b));
                results.Should().OnlyContain(lines => lines.Count(x => x.StartsWith("ack:")) == 150);
                results.Should().OnlyContain(lines => lines.Contains("count:150"));
                await a.Finish();
                await b.Finish();
            }

            using (var engine = new CoordinatedEngine(Filename))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                engine.IsCoordinator.Should().BeFalse();
                db.GetCollection("docs").Count().Should().Be(300);
                engine.DirectReads.Should().BeGreaterThan(0);
                await host.Finish(release: true);
                // The host exited; this process takes over on its next call.
                db.GetCollection("docs").Count().Should().Be(300);
                engine.IsCoordinator.Should().BeTrue();
                db.GetCollection("pending").Count().Should().Be(0, "the host rolled its transaction back");
            }
        }

        [Fact]
        public async Task A_killed_coordinator_process_keeps_every_acknowledged_write()
        {
            var host = new MvccProcess("coord-host", Filename, null);
            await host.Expect("ready");
            List<string> lines;
            using (var writer = new MvccProcess("coord-writer", Filename, null, "w:400"))
            {
                lines = new List<string>();
                while (lines.Count(x => x.StartsWith("ack:")) < 100) lines.Add(await writer.ReadLine(TimeSpan.FromSeconds(30)));
                await host.Kill();
                host.Dispose();
                lines.AddRange(await ReadToDone(writer));
                await writer.Finish();
            }

            var acknowledged = lines.Where(x => x.StartsWith("ack:")).Select(x => x.Substring(4)).ToList();
            var unknown = lines.Where(x => x.StartsWith("unknown:")).Select(x => x.Substring(8)).ToList();
            acknowledged.Count.Should().BeGreaterOrEqualTo(399);
            unknown.Count.Should().BeLessOrEqualTo(1);

            using var db = new LiteDatabase(Filename);
            var ids = db.GetCollection("docs").FindAll().Select(x => x["_id"].AsString).ToList();
            ids.Should().Contain(acknowledged);
            ids.Except(acknowledged).Should().BeSubsetOf(unknown);
            db.GetCollection("pending").Count().Should().Be(0, "the killed coordinator's transaction never committed");
            db.GetCollection("docs").Find(Query.EQ("n", 7)).Should().HaveCount(1, "the index agrees with the documents");
        }

        private static async Task<List<string>> ReadToDone(MvccProcess process)
        {
            var lines = new List<string>();
            while (true)
            {
                var line = await process.ReadLine(TimeSpan.FromSeconds(60));
                if (line == null) throw new InvalidOperationException("Writer exited early: " + string.Join(" | ", lines.TakeLast(5)));
                if (line == "done") return lines;
                lines.Add(line);
            }
        }

        public void Dispose()
        {
            try { Directory.Delete(_directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
#endif
