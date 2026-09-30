using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// A lazy input sequence runs inside its write, which retains the native mutex until the
    /// sequence ends. When the sequence calls another connection to the same database, that
    /// connection must refuse instead of waiting for the mutex: its wait would need the outer
    /// write to return first. Same-connection recursion and other databases stay valid.
    /// </summary>
    [Collection(nameof(SharedPeerCallbackCollection))]
    public class SharedPeerCallback_Tests : SharedPeerCallbackFixture
    {
        /// <summary>Unanchored: the outer write owns the mutex itself. Pinned: a leased reader of the thread pins it.</summary>
        public enum Route { Unanchored, Pinned }

        public enum Nested { PeerWrite, PeerRead }

        public enum Target { SameConnection, OtherDatabase }

        private static readonly bool[] Encryption = { false, true };

        public static IEnumerable<object[]> RefusalCases() =>
            from route in new[] { Route.Unanchored, Route.Pinned }
            from nested in new[] { Nested.PeerWrite, Nested.PeerRead }
            from encrypted in Encryption
            select new object[] { route, nested, encrypted };

        public static IEnumerable<object[]> RouteCases() =>
            from route in new[] { Route.Unanchored, Route.Pinned }
            from encrypted in Encryption
            select new object[] { route, encrypted };

        public static IEnumerable<object[]> ControlCases() =>
            from route in new[] { Route.Unanchored, Route.Pinned }
            from target in new[] { Target.SameConnection, Target.OtherDatabase }
            from encrypted in Encryption
            select new object[] { route, target, encrypted };

        [Theory]
        [MemberData(nameof(RefusalCases))]
        public void Peer_call_from_input_callback_is_refused_instead_of_waiting(Route route, Nested nested, bool encrypted)
        {
            this.Seed(this.Filename, encrypted);
            var outer = this.OpenOuter(encrypted);
            var peer = this.OpenPeer(this.Filename, encrypted);
            var retained = false;
            Exception refusal = null;

            var error = this.RunBounded(() => InsertWithCallback(outer, route, () =>
            {
                retained = Retains(outer, route);
                try
                {
                    if (nested == Nested.PeerWrite) peer.GetCollection("rows").Insert(Row(9));
                    else peer.GetCollection("rows").FindById(1);
                }
                catch (Exception ex)
                {
                    refusal = ex;
                    throw;
                }
            }));

            retained.Should().BeTrue("the callback must run while its outer write retains the native mutex");
            refusal.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("another connection");
            error.Should().BeSameAs(refusal, "the aborted outer write reports its callback's error");

            // The refusal poisons neither connection.
            peer.GetCollection("rows").Insert(Row(9));
            outer.Insert("rows", new[] { Row(4) }, BsonAutoId.Int32);
            this.CloseAll();
            VerifyCold(this.Filename, encrypted, 1, 4, 9);
        }

        [Theory]
        [MemberData(nameof(RouteCases))]
        public void Caught_refusal_lets_the_outer_write_complete(Route route, bool encrypted)
        {
            this.Seed(this.Filename, encrypted);
            var outer = this.OpenOuter(encrypted);
            var peer = this.OpenPeer(this.Filename, encrypted);
            Exception refusal = null;

            var error = this.RunBounded(() => InsertWithCallback(outer, route, () =>
            {
                try { peer.GetCollection("rows").Insert(Row(9)); }
                catch (InvalidOperationException ex) { refusal = ex; }
            }));

            error.Should().BeNull();
            refusal.Should().NotBeNull();
            peer.GetCollection("rows").Insert(Row(9));
            this.CloseAll();
            VerifyCold(this.Filename, encrypted, 1, 2, 3, 9);
        }

        [Theory]
        [MemberData(nameof(ControlCases))]
        public void Nested_write_through_same_connection_or_other_database_completes(Route route, Target target, bool encrypted)
        {
            this.Seed(this.Filename, encrypted);
            this.Seed(this.OtherFilename, encrypted);
            var outer = this.OpenOuter(encrypted);
            var other = this.OpenPeer(this.OtherFilename, encrypted);
            var retained = false;

            var error = this.RunBounded(() => InsertWithCallback(outer, route, () =>
            {
                retained = Retains(outer, route);
                if (target == Target.SameConnection) outer.Insert("rows", new[] { Row(9) }, BsonAutoId.Int32);
                else other.GetCollection("rows").Insert(Row(9));
            }));

            error.Should().BeNull();
            retained.Should().BeTrue();
            this.CloseAll();
            if (target == Target.SameConnection)
            {
                VerifyCold(this.Filename, encrypted, 1, 2, 3, 9);
                VerifyCold(this.OtherFilename, encrypted, 1);
            }
            else
            {
                VerifyCold(this.Filename, encrypted, 1, 2, 3);
                VerifyCold(this.OtherFilename, encrypted, 1, 9);
            }
        }

        /// <summary>Insert rows 2 and 3 through <paramref name="outer"/>, running <paramref name="callback"/> between them.</summary>
        private static void InsertWithCallback(SharedEngine outer, Route route, Action callback)
        {
            IBsonDataReader anchor = null;
            if (route == Route.Pinned)
            {
                // A leased reader holds no mutex; the thread's next write pins it.
                anchor = outer.Query("rows", new Query());
                anchor.Read().Should().BeTrue();
                outer.Pin.Should().BeNull("the anchor must be a leased reader");
            }
            try
            {
                outer.Insert("rows", Input(callback), BsonAutoId.Int32);
            }
            finally
            {
                anchor?.Dispose();
            }
        }

        private static IEnumerable<BsonDocument> Input(Action callback)
        {
            yield return Row(2);
            callback();
            yield return Row(3);
        }

        /// <summary>Whether the calling thread's outer write holds the mutex through <paramref name="route"/>.</summary>
        private static bool Retains(SharedEngine outer, Route route) => route == Route.Pinned
            ? outer.Pin?.IsOperatingOn(Thread.CurrentThread) == true && !outer.MutexOwner.IsOwnedByCurrentThread
            : outer.Pin == null && outer.MutexOwner.IsOwnedByCurrentThread;
    }
}
