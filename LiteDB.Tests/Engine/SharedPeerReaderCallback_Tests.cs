using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// A ReadTransform runs on every later Read of a reader, after its query returned. A
    /// reader streaming under the native mutex keeps it until disposed, so its callback
    /// cannot wait for the mutex through another connection. A leased reader holds no mutex,
    /// and an idle pin ends for a waiter: both must still let a peer connection proceed.
    /// </summary>
    [Collection(nameof(SharedPeerCallbackCollection))]
    public class SharedPeerReaderCallback_Tests : SharedPeerCallbackFixture
    {
        /// <summary>
        /// Owner: a write query keeps the connection's ownership. Pin: it holds the thread's pin.
        /// Unleased: without a reader lease, a plain read streams its snapshot under the ownership.
        /// </summary>
        public enum ReaderRoute { Owner, Pin, Unleased }

        private Action _onRead;

        public static IEnumerable<object[]> RouteCases() =>
            from route in new[] { ReaderRoute.Owner, ReaderRoute.Pin, ReaderRoute.Unleased }
            from encrypted in new[] { false, true }
            select new object[] { route, encrypted };

        /// <summary>Runs the pending callback once, from inside the next transformed read.</summary>
        private BsonValue Transform(string collection, BsonValue value)
        {
            var action = _onRead;
            _onRead = null;
            action?.Invoke();
            return value;
        }

        [Theory]
        [MemberData(nameof(RouteCases))]
        public void Peer_write_from_retaining_reader_callback_is_refused(ReaderRoute route, bool encrypted)
        {
            this.Seed(this.Filename, encrypted, rows: 3);
            var outer = this.OpenOuter(encrypted, this.Transform, unleased: route == ReaderRoute.Unleased);
            var peer = this.OpenPeer(this.Filename, encrypted);
            var retained = false;
            Exception refusal = null;
            var read = new List<int>();

            var error = this.RunBounded(() =>
            {
                IBsonDataReader anchor = null;
                if (route == ReaderRoute.Pin)
                {
                    anchor = outer.Query("rows", new Query());
                    anchor.Read().Should().BeTrue();
                    outer.Insert("rows", new[] { Row(4) }, BsonAutoId.Int32);
                    outer.Pin.Should().NotBeNull("a write of a thread with a leased reader pins");
                }
                try
                {
                    using var reader = outer.Query("rows", new Query { ForUpdate = route != ReaderRoute.Unleased });
                    reader.Read().Should().BeTrue();
                    read.Add(reader.Current["_id"].AsInt32);
                    _onRead = () =>
                    {
                        retained = route == ReaderRoute.Pin
                            ? outer.Pin != null && !outer.MutexOwner.IsOwnedByCurrentThread
                            : outer.Pin == null && outer.MutexOwner.IsOwnedByCurrentThread;
                        try { peer.GetCollection("rows").Insert(Row(9)); }
                        catch (Exception ex) { refusal = ex; }
                    };
                    while (reader.Read()) read.Add(reader.Current["_id"].AsInt32);
                }
                finally
                {
                    anchor?.Dispose();
                }
            });

            error.Should().BeNull();
            retained.Should().BeTrue("the reader must retain the native mutex while its callback runs");
            refusal.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("another connection");
            var expected = route == ReaderRoute.Pin ? new[] { 1, 2, 3, 4 } : new[] { 1, 2, 3 };
            read.Should().Equal(expected, "a refused callback does not disturb the reader");

            peer.GetCollection("rows").Insert(Row(9));
            this.CloseAll();
            VerifyCold(this.Filename, encrypted, expected.Concat(new[] { 9 }).ToArray());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Leased_reader_callback_may_write_through_peer(bool encrypted)
        {
            this.Seed(this.Filename, encrypted, rows: 3);
            var outer = this.OpenOuter(encrypted, this.Transform);
            var peer = this.OpenPeer(this.Filename, encrypted);
            var leased = false;
            var read = new List<int>();

            var error = this.RunBounded(() =>
            {
                using var reader = outer.Query("rows", new Query());
                reader.Read().Should().BeTrue();
                read.Add(reader.Current["_id"].AsInt32);
                _onRead = () =>
                {
                    leased = outer.Pin == null && !outer.MutexOwner.IsOwnedByCurrentThread;
                    peer.GetCollection("rows").Insert(Row(9));
                };
                while (reader.Read()) read.Add(reader.Current["_id"].AsInt32);
            });

            error.Should().BeNull();
            leased.Should().BeTrue("an independent leased snapshot holds no native ownership");
            read.Should().Equal(1, 2, 3);
            this.CloseAll();
            VerifyCold(this.Filename, encrypted, 1, 2, 3, 9);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Idle_pin_ends_for_a_later_peer_write_on_its_thread(bool encrypted)
        {
            this.Seed(this.Filename, encrypted);
            var outer = this.OpenOuter(encrypted);
            var peer = this.OpenPeer(this.Filename, encrypted);
            var idle = false;

            var error = this.RunBounded(() =>
            {
                using var anchor = outer.Query("rows", new Query());
                anchor.Read().Should().BeTrue();
                outer.Insert("rows", new[] { Row(4) }, BsonAutoId.Int32);
                var pin = outer.Pin;
                idle = pin != null && !pin.IsOperatingOn(Thread.CurrentThread);
                // Not inside the outer operation: the pin is idle, and its limits never expire
                // here, so only the peer's wait ends it. Waiting is valid; refusing is not.
                peer.GetCollection("rows").Insert(Row(9));
            });

            error.Should().BeNull();
            idle.Should().BeTrue("the pin must be idle, not ended, when the peer starts waiting");
            this.CloseAll();
            VerifyCold(this.Filename, encrypted, 1, 4, 9);
        }
    }
}
