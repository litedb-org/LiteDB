using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
            using var listener = new RepairListener(throws);
            Trace.Listeners.Add(listener);
            try
            {
                var docs = db.GetCollection<VectorDocument>("docs");
                docs.Delete(1).Should().BeTrue();
                RepairDeferred(db).Should().BeTrue();
                for (var i = 0; i < 2; i++)
                    docs.Query().TopKNear(x => x.Embedding, new[] { 1f, 2f }, 10).ToArray().Should().HaveCount(10);
                RetrySmallGraph(docs, true);
                RepairDeferred(db).Should().BeFalse();
                docs.FindById(2).Embedding.Should().Equal(new[] { 1f, -2f });
                listener.Warnings.Should().Be(1);
            }
            finally { Trace.Listeners.Remove(listener); }
        }

        private sealed class RepairListener : TraceListener
        {
            private readonly bool _throws;
            public int Warnings { get; private set; }
            internal RepairListener(bool throws) { _throws = throws; }
            public override void Write(string message) { }
            public override void WriteLine(string message) { }
            public override void TraceEvent(TraceEventCache cache, string source, TraceEventType type, int id, string message)
            {
                if (!message.Contains("repair deferred")) return;
                Warnings++;
                if (_throws) throw new InvalidOperationException("Application trace listener failed");
            }
        }
    }
}
