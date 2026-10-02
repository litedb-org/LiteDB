using System;
using System.Reflection;
using System.Threading;
using LiteDB.Engine;
using LiteDB.Utils;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// Reproduction probe (V-waitgraph, row 13): the fix's scenario (an active read whose callback waits for
    /// fresh work on another thread while a raw close queues), with the callback's wait registered as a
    /// driver edge (<see cref="WaitGraph.Join"/>), as a harness would. Variant bounded: the callback waits at
    /// most 2 s, as in the fix's test. Variant unbounded: the edge models an application callback that waits
    /// without a bound; the probe still caps the real join at 10 s so the run ends.
    /// </summary>
    public class WaitGraphRepro_Row13Driver_Tests
    {
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Callback_waiting_for_fresh_work_while_raw_close_queues(bool bounded)
        {
            using var file = new TempFile();
            using var callbackEntered = new ManualResetEventSlim();
            using var startDependency = new ManualResetEventSlim();
            LiteDatabase db = null;
            Exception dependencyError = null;
            var dependencyFinished = false;
            var engine = new LiteEngine(new EngineSettings
            {
                Filename = file,
                ReadTransform = (collection, value) =>
                {
                    if (collection != "rows") return value;
                    callbackEntered.Set();
                    Assert.True(startDependency.Wait(TimeSpan.FromSeconds(10)));
                    var dependency = new Thread(() =>
                    {
                        try { db.GetCollection("untouched").Count(); }
                        catch (Exception ex) { dependencyError = ex; }
                    }) { IsBackground = true, Name = "row13-dependency" };
                    dependency.Start();
                    var bound = bounded ? WaitBound.After(TimeSpan.FromSeconds(2)) : WaitBound.Unbounded;
                    using (WaitGraph.Join(dependency, bound, "callback waits for its dependency"))
                        dependencyFinished = dependency.Join(TimeSpan.FromSeconds(bounded ? 2 : 10));
                    return value;
                }
            });
            db = new LiteDatabase(engine, disposeOnClose: false);
            db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = 10 });
            db.GetCollection("untouched").Insert(new BsonDocument { ["_id"] = 42 });
            Exception readError = null;
            var read = new Thread(() =>
            {
                try { db.GetCollection("rows").FindById(1); }
                catch (Exception ex) { readError = ex; }
            }) { IsBackground = true, Name = "row13-read" };
            read.Start();
            Assert.True(callbackEntered.Wait(TimeSpan.FromSeconds(10)));
            var close = new Thread(() => { try { engine.Dispose(); } catch { } }) { IsBackground = true, Name = "row13-close" };
            close.Start();
            try
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var operations = typeof(LiteEngine).GetField("_operations", flags).GetValue(engine);
                Assert.True(SpinWait.SpinUntil(() =>
                    (int)operations.GetType().GetField("_waitingExclusive", flags).GetValue(operations) != 0,
                    TimeSpan.FromSeconds(10)));
            }
            finally { startDependency.Set(); }
            Assert.True(read.Join(TimeSpan.FromSeconds(20)));
            Assert.True(close.Join(TimeSpan.FromSeconds(20)));
            Assert.True(dependencyFinished, "Close must reject fresh work before waiting for an active callback that needs it.");
        }
    }
}
