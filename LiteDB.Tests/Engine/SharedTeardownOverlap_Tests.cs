#if DEBUG || TESTING
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    [CollectionDefinition(Name, DisableParallelization = true)]
    public class SharedTeardownOverlapCollection
    {
        internal const string Name = "SharedTeardownOverlap";
    }

    /// <summary>
    /// Disposing a shared connection must never close an engine under one of its own
    /// operations. An engine closed under a live transaction cannot dispose its busy page
    /// cache: the transaction's writable page leaks (finalized with share count -1, the
    /// writable marker) and, in TESTING builds, the finalizer's assertion ends the process.
    /// These tests count such buffers through a hook instead.
    /// </summary>
    [Collection(SharedTeardownOverlapCollection.Name)]
    public class SharedTeardownOverlap_Tests : IDisposable
    {
        private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan Forever = TimeSpan.FromMinutes(10);
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedb-teardown-" + Guid.NewGuid().ToString("N"));
        private readonly List<int> _leaks = new List<int>();

        public SharedTeardownOverlap_Tests()
        {
            Directory.CreateDirectory(_directory);
            CollectFinalizers();
            PageBuffer.FinalizedInUse = count => { lock (_leaks) _leaks.Add(count); };
        }

        public void Dispose()
        {
            // Leaks of a failed run are counted, not left to end a later test's process.
            CollectFinalizers();
            PageBuffer.FinalizedInUse = null;
            try { Directory.Delete(_directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static BsonDocument Doc(int id, int value) =>
            new BsonDocument { ["_id"] = id, ["value"] = value, ["payload"] = new string('p', 200) };

        private string Seed(int round)
        {
            var file = Path.Combine(_directory, $"db{round}.db");
            using var seed = new LiteDatabase(new ConnectionString { Filename = file, Connection = ConnectionType.Direct });
            seed.GetCollection("docs").Insert(Enumerable.Range(1, 200).Select(id => Doc(id, 0)));
            return file;
        }

        /// <summary>
        /// The observed Linux path: a thread of the connection waits behind a pin; Dispose ends
        /// the pin; the waiter is admitted while Dispose is still closing the connection.
        /// </summary>
        [Fact]
        public void A_waiter_admitted_during_Dispose_neither_runs_on_a_closing_engine_nor_leaks_one()
        {
            for (var round = 0; round < 20; round++)
            {
                var file = this.Seed(round);
                this.WaiterAdmittedDuringDispose(file, out var waiterError);
                // The waiter either completed first or was refused as disposed; never anything else.
                if (waiterError != null) waiterError.Should().BeOfType<ObjectDisposedException>();
                CollectFinalizers();
                // No engine of the disposed connection keeps the files open.
                using (var probe = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            }
            lock (_leaks) _leaks.Should().BeEmpty("no page buffer may be finalized while in use");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void WaiterAdmittedDuringDispose(string file, out Exception waiterError)
        {
            var engine = new SharedEngine(new EngineSettings { Filename = file });
            engine.PinIdleLimit = Forever;
            engine.PinHoldLimit = Forever;
            var reader = engine.Query("docs", new Query());
            reader.Read().Should().BeTrue();
            engine.Update("docs", new[] { Doc(1, 1) });

            Exception error = null;
            var waiter = new Thread(() =>
            {
                try
                {
                    for (var i = 0; i < 50; i++)
                        engine.Insert("other", new[] { new BsonDocument { ["_id"] = i + 1, ["p"] = new string('x', 3000) } }, BsonAutoId.Int32);
                }
                catch (Exception ex) { error = ex; }
            }) { IsBackground = true };
            waiter.Start();
            Thread.Sleep(50);

            engine.Dispose();
            waiter.Join(Prompt).Should().BeTrue();
            reader.Dispose();
            waiterError = error;
        }

        /// <summary>Dispose while other threads of the connection run tight write and read loops.</summary>
        [Fact]
        public void Dispose_racing_tight_loops_of_the_connection_leaks_no_engine_or_page()
        {
            for (var round = 0; round < 20; round++)
            {
                var file = this.Seed(100 + round);
                this.DisposeDuringLoops(file);
                CollectFinalizers();
                using (var probe = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            }
            lock (_leaks) _leaks.Should().BeEmpty("no page buffer may be finalized while in use");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void DisposeDuringLoops(string file)
        {
            var engine = new SharedEngine(new EngineSettings { Filename = file });
            using var stop = new ManualResetEventSlim();
            var errors = new List<Exception>();
            var threads = Enumerable.Range(0, 3).Select(t => new Thread(() =>
            {
                try
                {
                    for (var i = 0; !stop.IsSet; i++)
                    {
                        if (t == 0) engine.Update("docs", new[] { Doc(1 + i % 200, i) });
                        else if (t == 1) engine.Insert("more", new[] { new BsonDocument { ["_id"] = i + 1 } }, BsonAutoId.Int32);
                        else engine.Query("docs", new Query()).ToEnumerable().Count();
                    }
                }
                catch (Exception ex) { lock (errors) errors.Add(ex); }
            }) { IsBackground = true }).ToList();
            threads.ForEach(x => x.Start());
            Thread.Sleep(30);
            engine.Dispose();
            stop.Set();
            threads.ForEach(x => x.Join(Prompt).Should().BeTrue());
            // Operations after Dispose fail; they must fail as a disposed connection.
            lock (errors)
                errors.Where(ex => !(ex is ObjectDisposedException)).Should().BeEmpty();
        }

        private static void CollectFinalizers()
        {
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
    }
}
#endif
