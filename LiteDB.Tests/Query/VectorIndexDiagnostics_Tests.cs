using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using FluentAssertions;
using LiteDB.Tests.Engine;
using LiteDB.Vector;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public partial class VectorIndex_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Deferred_transition_logs_once_and_throwing_listener_cannot_abort_documents(bool throws)
        {
            using var file = new TempFile();
            File.WriteAllBytes(file.Filename, AsymmetricFixture());
            using var db = new LiteDatabase(file.Filename);
            var collection = "trace_" + Guid.NewGuid().ToString("N");
            db.RenameCollection("docs", collection).Should().BeTrue();
            using var listener = new RepairListener(collection, throws);
            Trace.Listeners.Add(listener);
            try
            {
                var docs = db.GetCollection<VectorDocument>(collection);
                docs.Delete(1).Should().BeTrue();
                listener.Completed.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                RepairDeferred(db, collection).Should().BeTrue();
                for (var i = 0; i < 2; i++)
                    docs.Query().TopKNear(x => x.Embedding, new[] { 1f, 2f }, 10).ToArray().Should().HaveCount(10);
                RetrySmallGraph(docs, true);
                RepairDeferred(db, collection).Should().BeFalse();
                docs.FindById(2).Embedding.Should().Equal(new[] { 1f, -2f });
                listener.Warnings.Should().Be(1);
            }
            finally { Trace.Listeners.Remove(listener); }
        }

        [Fact]
        public void Warning_listener_write_cannot_reenter_an_incomplete_document_mutation()
        {
            using var file = new TempFile();
            File.WriteAllBytes(file.Filename, AsymmetricFixture());
            using var db = new LiteDatabase(file.Filename);
            var collection = "trace_" + Guid.NewGuid().ToString("N");
            db.RenameCollection("docs", collection).Should().BeTrue();
            var docs = db.GetCollection<VectorDocument>(collection);
            var writerThread = Environment.CurrentManagedThreadId;
            Exception callbackError = null;
            using var listener = new RepairListener(collection, false, () =>
            {
                try { docs.Insert(new VectorDocument { Id = 74, Embedding = new[] { 1f, 74f } }); }
                catch (Exception error) { callbackError = error; }
            });
            Trace.Listeners.Add(listener);
            try
            {
                docs.Delete(1).Should().BeTrue();
                listener.Completed.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                listener.CallbackThread.Should().NotBe(writerThread);
                callbackError.Should().BeNull();
                RepairDeferred(db, collection).Should().BeFalse();
                docs.Count().Should().Be(73);
                var target = new[] { 1f, 40f };
                docs.Query().TopKNear(x => x.Embedding, target, 74).ToArray().Select(x => x.Id).Should()
                    .Equal(ComputeExpectedRanking(docs.FindAll(), target, VectorDistanceMetric.Cosine).Select(x => x.Id));
                listener.Warnings.Should().Be(1);
            }
            finally { Trace.Listeners.Remove(listener); }
        }

        private sealed class RepairListener : TraceListener
        {
            private readonly string _collection;
            private readonly bool _throws;
            private readonly Action _action;
            private int _warnings;
            public int Warnings => Volatile.Read(ref _warnings);
            public int CallbackThread { get; private set; }
            internal ManualResetEventSlim Completed { get; } = new ManualResetEventSlim();
            internal RepairListener(string collection, bool throws, Action action = null)
            {
                _collection = collection;
                _throws = throws;
                _action = action;
            }
            public override void Write(string message) { }
            public override void WriteLine(string message) { }
            public override void TraceEvent(TraceEventCache cache, string source, TraceEventType type, int id, string message)
            {
                if (!message.Contains(_collection) || !message.Contains("repair deferred")) return;
                try
                {
                    CallbackThread = Environment.CurrentManagedThreadId;
                    Interlocked.Increment(ref _warnings);
                    _action?.Invoke();
                    if (_throws) throw new InvalidOperationException("Application trace listener failed");
                }
                finally { Completed.Set(); }
            }
            protected override void Dispose(bool disposing)
            {
                if (disposing) Completed.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
