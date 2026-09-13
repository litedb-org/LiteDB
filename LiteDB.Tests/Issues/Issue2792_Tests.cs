using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LiteDB.Engine;
using FluentAssertions.Execution;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2792_Tests
    {
        [Fact]
        public void Reader_during_collection_header_publication_never_sees_unconfirmed_page()
        {
            using var file = new TempFile();
            using var engine = new LiteEngine(new EngineSettings { Filename = file.Filename });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            using var headerReached = new ManualResetEventSlim();
            using var releaseHeader = new ManualResetEventSlim();
            using var readerStarted = new ManualResetEventSlim();
            engine.SimulateDiskWriteFail = page =>
            {
                if (page.ReadUInt32(BasePage.P_PAGE_ID) != 0) return;
                headerReached.Set();
                if (!releaseHeader.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("header gate");
            };
            var writer = Task.Run(() => Record.Exception(() =>
            {
                db.BeginTrans().Should().BeTrue();
                db.GetCollection("new_collection").Insert(new BsonDocument { ["_id"] = "QueryHash", ["value"] = "published" });
                db.Commit().Should().BeTrue();
            }));
            Task<Exception> reader = null;
            try
            {
                headerReached.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                reader = Task.Run(() => Record.Exception(() =>
                {
                    readerStarted.Set();
                    var found = db.GetCollection("new_collection").FindById("QueryHash");
                    if (found != null) found["value"].AsString.Should().Be("published");
                }));
                readerStarted.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                // A fix may block until publication. Give an unlocked reader a chance to execute.
                reader.Wait(TimeSpan.FromMilliseconds(200));
            }
            finally
            {
                releaseHeader.Set();
                writer.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                if (reader != null) reader.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                engine.SimulateDiskWriteFail = null;
            }
            using (new AssertionScope())
            {
                writer.Result.Should().BeNull();
                reader.Result.Should().BeNull();
            }
            db.GetCollection("new_collection").FindById("QueryHash")["value"].AsString.Should().Be("published");
            db.Checkpoint();
            engine.Dispose();
            using var reopened = new LiteDatabase(file.Filename);
            reopened.GetCollection("new_collection").FindAll().Single()["value"].AsString.Should().Be("published");
        }
    }
}
