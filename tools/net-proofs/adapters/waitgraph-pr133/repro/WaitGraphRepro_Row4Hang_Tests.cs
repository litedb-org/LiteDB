using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// Reproduction probe (V-waitgraph, row 4): the fix's scenario with a parameterless begin (the
    /// documented "can wait forever" overload) instead of TimeSpan.Zero, bounded by the test at 10 s.
    /// </summary>
    public class WaitGraphRepro_Row4Hang_Tests
    {
#pragma warning disable CS0618
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Parameterless_begin_under_callers_reader_or_pinned_legacy_transaction(bool legacy)
        {
            var file = new TempFile();
            Exception error = null;
            // Everything runs on one thread, as in the fix's test; the test thread only bounds it.
            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, Connection = ConnectionType.Shared });
                    var rows = db.GetCollection("rows");
                    rows.Insert(Enumerable.Range(0, 150).Select(i => new BsonDocument { ["_id"] = i }));
                    var reader = db.Execute(legacy ? "SELECT $ FROM rows" : "SELECT $ FROM rows FOR UPDATE");
                    Assert.True(reader.Read());
                    if (legacy)
                    {
                        rows.Insert(new BsonDocument { ["_id"] = 200 });
                        Assert.True(db.BeginTrans());
                        rows.Insert(new BsonDocument { ["_id"] = 201 });
                    }
                    using (db.BeginTransaction()) { }
                    reader.Dispose();
                    db.Dispose();
                }
                catch (Exception ex) { error = ex; }
            }) { IsBackground = true, Name = "row4-probe" };
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "parameterless begin under the caller's own reader/pin did not progress within 10 s");
            Assert.True(error == null || error is InvalidOperationException, error?.ToString());
        }
#pragma warning restore CS0618
    }
}
