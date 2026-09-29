#if DEBUG || TESTING
using System;
using System.IO;
using System.Linq;
using System.Threading;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Internals;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// A write or sync failure stops the engine: every later operation throws the original failure,
    /// nothing more is written, and a cold reopen shows exactly the state acknowledged before it.
    /// </summary>
    [Trait("Category", "IoSafety")]
    public class DurabilityPolicy_Tests
    {
        private const int Rows = 16;

        /// <summary>
        /// Another thread's commit (its WAL write fails) while this thread's explicit transaction is
        /// open. The failure stopped the engine and ended that transaction: its Commit throws the
        /// original failure, and a cold reopen shows exactly the committed rows and none of its changes.
        /// </summary>
        [Fact]
        public void Explicit_transaction_ended_by_another_threads_failure_throws_at_commit()
        {
            using var file = new TempFile();
            Setup(file.Filename);
            var logName = FileHelper.GetLogFile(file.Filename);
            try
            {
                using (var engine = new LiteEngine(new EngineSettings { Filename = file.Filename }))
                using (var db = new LiteDatabase(engine, disposeOnClose: false))
                {
                    db.CheckpointSize = 0;
                    Update(db, 1);

                    db.BeginTrans().Should().BeTrue();
                    db.GetCollection("pending").Insert(new BsonDocument { ["_id"] = 1 });

                    var failing = new Thread(() =>
                    {
                        engine.SimulateDiskWriteFail = _ =>
                        {
                            if (Thread.CurrentThread.Name == "failing") throw new IOException("injected WAL write failure");
                        };
                        Action commit = () => Update(db, 2);
                        commit.Should().Throw<IOException>().WithMessage("injected WAL write failure");
                    }) { Name = "failing" };
                    failing.Start();
                    failing.Join();

                    Action complete = () => db.Commit();
                    var thrown = complete.Should().Throw<IOException>().Which;
                    thrown.Message.Should().StartWith("Engine closed after an I/O failure");
                    thrown.InnerException.Should().BeOfType<IOException>().Which.Message.Should().Be("injected WAL write failure",
                        "the stopped engine throws the original failure");
                    Action read = () => db.GetCollection("rows").FindAll().ToList();
                    read.Should().Throw<IOException>().WithMessage("Engine closed after an I/O failure*");
                }

                using (var reopened = new LiteDatabase(file.Filename))
                {
                    reopened.CollectionExists("pending").Should().BeFalse("the ended transaction left nothing");
                    AssertRows(reopened, 1);
                }
            }
            finally { File.Delete(logName); }
        }

        private static void Setup(string filename)
        {
            using var setup = new LiteDatabase(filename);
            setup.GetCollection("rows").Insert(Enumerable.Range(1, Rows).Select(id => MvccRetirementScenario.Document(id, 0)));
            setup.GetCollection("rows").EnsureIndex("value");
        }

        private static void Update(LiteDatabase db, int value) =>
            db.GetCollection("rows").Upsert(Enumerable.Range(1, Rows).Select(id => MvccRetirementScenario.Document(id, value)));

        /// <summary>Exactly <see cref="Rows"/> rows, each holding <paramref name="value"/>, also through the value index.</summary>
        private static void AssertRows(LiteDatabase db, int value)
        {
            var rows = db.GetCollection("rows");
            rows.FindAll().OrderBy(x => x["_id"].AsInt32).Should().BeEquivalentTo(
                Enumerable.Range(1, Rows).Select(id => MvccRetirementScenario.Document(id, value)), o => o.WithStrictOrdering());
            rows.Find(Query.EQ("value", value)).Select(x => x["_id"].AsInt32).Should().BeEquivalentTo(Enumerable.Range(1, Rows));
            rows.Count(Query.Not("value", value)).Should().Be(0);
        }
    }
}
#endif
