using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    [CollectionDefinition(nameof(SharedPeerCallbackCollection), DisableParallelization = true)]
    public sealed class SharedPeerCallbackCollection
    {
    }

    /// <summary>
    /// Two Shared connections to one database. The outer connection runs a user callback
    /// while it retains the native mutex; the callback calls a peer connection on the same
    /// thread. A worker that does not return is the deadlock under test: its connections
    /// stay open and its files stay on disk, because disposing a live graph would block
    /// the test host on the same ownership.
    /// </summary>
    public abstract class SharedPeerCallbackFixture : IDisposable
    {
        protected static readonly TimeSpan Prompt = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan Forever = TimeSpan.FromMinutes(10);
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedb-peer-" + Guid.NewGuid().ToString("N"));
        private readonly List<IDisposable> _open = new List<IDisposable>();
        private bool _retain;

        protected SharedPeerCallbackFixture() => Directory.CreateDirectory(_directory);

        protected string Filename => Path.Combine(_directory, "outer.db");

        protected string OtherFilename => Path.Combine(_directory, "other.db");

        protected static string Password(bool encrypted) => encrypted ? "secret" : null;

        protected static BsonDocument Row(int id) => new BsonDocument { ["_id"] = id, ["value"] = id };

        /// <summary>Rows 1..<paramref name="rows"/> indexed on value, and an unrelated sentinel.</summary>
        protected void Seed(string filename, bool encrypted, int rows = 1)
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = filename, Password = Password(encrypted), Connection = ConnectionType.Direct });
            var col = db.GetCollection("rows");
            col.Insert(Enumerable.Range(1, rows).Select(Row));
            col.EnsureIndex("value");
            db.GetCollection("sentinel").Insert(Row(42));
        }

        /// <summary>
        /// The outer connection. A ReadTransform (by default the identity) makes every write
        /// with a lazy input and every read able to run user code under the mutex. Pins end
        /// only when requested or for a waiter, never at their idle or total limit. An
        /// <paramref name="unleased"/> connection cannot register reader leases, so its readers
        /// stream under the mutex.
        /// </summary>
        protected SharedEngine OpenOuter(bool encrypted, Func<string, BsonValue, BsonValue> transform = null, bool unleased = false)
        {
            var settings = new EngineSettings
            {
                Filename = this.Filename,
                Password = Password(encrypted),
                ReadTransform = transform ?? ((_, value) => value)
            };
            if (unleased) settings.SharedReaderFiles = (_, __) => throw new UnauthorizedAccessException("registry denied");
            var engine = new SharedEngine(settings);
            engine.PinIdleLimit = Forever;
            engine.PinHoldLimit = Forever;
            return this.Track(engine);
        }

        protected LiteDatabase OpenPeer(string filename, bool encrypted) => this.OpenPeer(filename, encrypted, out _);

        protected LiteDatabase OpenPeer(string filename, bool encrypted, out SharedEngine engine)
        {
            engine = new SharedEngine(new EngineSettings { Filename = filename, Password = Password(encrypted) });
            return this.Track(new LiteDatabase(engine));
        }

        protected T Track<T>(T disposable) where T : IDisposable
        {
            _open.Add(disposable);
            return disposable;
        }

        /// <summary>Run on a new thread and return its error. A thread that does not return fails the test.</summary>
        protected Exception RunBounded(Action action)
        {
            Exception error = null;
            var worker = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { error = ex; }
            }) { IsBackground = true };
            worker.Start();
            if (!worker.Join(Prompt))
            {
                _retain = true;
                throw new Xunit.Sdk.XunitException(
                    $"The worker did not return within {Prompt}: a nested call waits for ownership that its own outer " +
                    $"operation retains. Connections left open, files retained in {_directory}.");
            }
            return error;
        }

        protected void CloseAll()
        {
            for (var i = _open.Count - 1; i >= 0; i--) _open[i].Dispose();
            _open.Clear();
        }

        /// <summary>
        /// Cold reopen: exactly <paramref name="ids"/>, each found through its index, and the
        /// untouched sentinel. An aborted outer operation must leave none of its rows.
        /// </summary>
        protected static void VerifyCold(string filename, bool encrypted, params int[] ids)
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = filename, Password = Password(encrypted), Connection = ConnectionType.Direct });
            var rows = db.GetCollection("rows");
            rows.FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x).Should().Equal(ids);
            foreach (var id in ids)
            {
                var query = rows.Query().Where(Query.EQ("value", id));
                query.GetPlan()["index"]["name"].AsString.Should().Be("value");
                query.ToDocuments().Select(x => x["_id"].AsInt32).Should().Equal(id);
            }
            db.GetCollection("sentinel").FindById(42)["value"].AsInt32.Should().Be(42);
        }

        public void Dispose()
        {
            if (_retain) return;
            this.CloseAll();
            try { Directory.Delete(_directory, true); }
            catch (IOException) { /* A later run cleans the temp directory. */ }
        }
    }
}
