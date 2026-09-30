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
    /// <summary>
    /// A caller-owned stream is user code too, and closing an engine calls it: its close
    /// checkpoint writes the data file. Several closes run while the connection still holds
    /// the native mutex but outside any public call: disposing a result streamed under the
    /// mutex, a pin ending on its holder thread, the checkpoint after the last leased reader,
    /// and disposing the connection. A stream callback there must not wait for the mutex
    /// through another connection to the same database either.
    /// </summary>
    [Collection(nameof(SharedPeerCallbackCollection))]
    public class SharedPeerCloseCallback_Tests : SharedPeerCallbackFixture
    {
        // Past the 50-page close threshold, so the closing engine checkpoints.
        private const int BigRows = 60;

        public enum Close { RetainingReader, PinHolder, LastLeasedReader, ConnectionWithEngine, ConnectionCheckpoint }

        public static IEnumerable<object[]> Cases() =>
            from close in new[] { Close.RetainingReader, Close.PinHolder, Close.LastLeasedReader, Close.ConnectionWithEngine, Close.ConnectionCheckpoint }
            from encrypted in new[] { false, true }
            select new object[] { close, encrypted };

        private static BsonDocument Big(int id)
        {
            var row = Row(id);
            row["payload"] = new string('p', 4000);
            return row;
        }

        private static IEnumerable<BsonDocument> BigBatch(int first) => Enumerable.Range(first, BigRows).Select(Big);

        [Theory]
        [MemberData(nameof(Cases))]
        public void Peer_write_from_stream_callback_during_close_is_refused(Close close, bool encrypted)
        {
            this.Seed(this.Filename, encrypted);
            var data = this.Track(new CallbackFile(this.Filename));
            var log = this.Track(new CallbackFile(FileHelper.GetLogFile(this.Filename)));
            var outer = this.Track(new SharedEngine(new EngineSettings
            {
                Filename = this.Filename,
                Password = Password(encrypted),
                DataStream = data,
                LogStream = log,
                // A user callback: even a one-row read then streams under a lease instead of buffering.
                ReadTransform = (_, value) => value
            }));
            var peer = this.OpenPeer(this.Filename, encrypted);
            var called = 0;
            Exception refusal = null;
            Action callback = () =>
            {
                Interlocked.Increment(ref called);
                try { peer.GetCollection("rows").Insert(Row(9)); }
                catch (Exception ex) { refusal = ex; }
            };
            var expected = new List<int> { 1 };

            var error = this.RunBounded(() =>
            {
                switch (close)
                {
                    case Close.RetainingReader:
                    {
                        // A write query keeps the ownership; writes on its thread join its engine,
                        // which closes when the reader is disposed.
                        var reader = outer.Query("rows", new Query { ForUpdate = true });
                        outer.Insert("rows", BigBatch(100).ToArray(), BsonAutoId.Int32);
                        data.Arm(callback);
                        reader.Dispose();
                        break;
                    }
                    case Close.PinHolder:
                    {
                        // The pin's holder thread closes its engine once the last leased reader ends.
                        var anchor = outer.Query("rows", new Query());
                        anchor.Read().Should().BeTrue();
                        outer.Insert("rows", BigBatch(100).ToArray(), BsonAutoId.Int32);
                        data.Arm(callback);
                        anchor.Dispose();
                        break;
                    }
                    case Close.LastLeasedReader:
                    {
                        // The last leased reader's disposal checkpoints what another connection left.
                        var reader = outer.Query("rows", new Query());
                        reader.Read().Should().BeTrue();
                        peer.GetCollection("rows").Insert(BigBatch(100));
                        data.Arm(callback);
                        reader.Dispose();
                        break;
                    }
                    case Close.ConnectionWithEngine:
                    {
                        // Disposing the connection closes the engine a still-open result keeps.
                        var reader = outer.Query("rows", new Query { ForUpdate = true });
                        outer.Insert("rows", BigBatch(100).ToArray(), BsonAutoId.Int32);
                        data.Arm(callback);
                        outer.Dispose();
                        reader.Dispose();
                        break;
                    }
                    case Close.ConnectionCheckpoint:
                    {
                        // Disposing the connection checkpoints a WAL below the close threshold.
                        outer.Insert("rows", new[] { Row(100) }, BsonAutoId.Int32);
                        data.Arm(callback);
                        outer.Dispose();
                        break;
                    }
                }
            });

            error.Should().BeNull();
            called.Should().Be(1, "the close must write through the caller's data stream");
            refusal.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("another connection");
            expected.AddRange(close == Close.ConnectionCheckpoint ? new[] { 100 } : Enumerable.Range(100, BigRows));

            peer.GetCollection("rows").Insert(Row(9));
            this.CloseAll();
            VerifyCold(this.Filename, encrypted, expected.Concat(new[] { 9 }).OrderBy(x => x).ToArray());
        }

        /// <summary>The database's own file, shared with the peer connection, calling back on its first write: a checkpoint's, since read-only snapshots only read and flush.</summary>
        private sealed class CallbackFile : FileStream
        {
            private Action _onWrite;

            internal CallbackFile(string path)
                : base(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete)
            {
            }

            internal void Arm(Action action) => Volatile.Write(ref _onWrite, action);

            private void Fire() => Interlocked.Exchange(ref _onWrite, null)?.Invoke();

            public override void Write(byte[] array, int offset, int count)
            {
                this.Fire();
                base.Write(array, offset, count);
            }

#if NETCOREAPP
            public override void Write(ReadOnlySpan<byte> buffer)
            {
                this.Fire();
                base.Write(buffer);
            }
#endif
        }
    }
}
