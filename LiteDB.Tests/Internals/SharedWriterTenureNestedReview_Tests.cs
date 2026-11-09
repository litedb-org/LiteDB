#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedWriterTenureNestedReview_Tests
    {
        [OneFileFact]
        public void Nested_begin_does_not_wait_for_a_local_reservation_waiting_on_its_own_operation()
        {
            using var files = new SharedOneFilePublicationFixture();
            using (var engine = new SharedEngine(files.Settings))
            using (var inputEntered = new ManualResetEventSlim())
            using (var requested = new ManualResetEventSlim())
            using (var nestedEntered = new ManualResetEventSlim())
            using (var nestedReturned = new ManualResetEventSlim())
            using (var cancelWaiter = new ManualResetEventSlim())
            {
                Exception outerFailure = null, waiterFailure = null;
                bool? joined = null;
                var outer = new Thread(() => outerFailure = Record.Exception(() =>
                {
                    IEnumerable<BsonDocument> Input()
                    {
                        inputEntered.Set();
                        requested.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                        engine.Pin.IsOperatingOn(Thread.CurrentThread).Should().BeTrue();
                        nestedEntered.Set();
                        joined = engine.BeginTrans();
                        nestedReturned.Set();
                        yield return Row(1000, 1);
                    }
                    engine.Insert("admitted", Input(), BsonAutoId.Int32).Should().Be(1);
                })) { IsBackground = true };
                var waiter = new Thread(() =>
                {
                    SharedWriterRequests.RequestStage = stage =>
                    {
                        if (stage != "request-published") return;
                        requested.Set();
                        if (!cancelWaiter.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("review cancellation barrier");
                        throw new IOException("cancel reserved waiter for test cleanup");
                    };
                    try { waiterFailure = Record.Exception(() => engine.BeginTrans()); }
                    finally { SharedWriterRequests.RequestStage = null; }
                }) { IsBackground = true };
                var returnedWithoutCancellation = false;
                outer.Start();
                try
                {
                    inputEntered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                    waiter.Start();
                    nestedEntered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                    var reservation = Field<SharedLocalTransactionOwner.Lease>(engine.MappedReaderTable.LocalTransactions, "_current");
                    reservation.Thread.Should().BeSameAs(waiter);
                    returnedWithoutCancellation = nestedReturned.Wait(TimeSpan.FromSeconds(1));
                }
                finally
                {
                    cancelWaiter.Set();
                    if ((waiter.ThreadState & ThreadState.Unstarted) == 0) waiter.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
                    outer.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
                }
                outerFailure.Should().BeNull();
                waiterFailure.Should().BeOfType<IOException>().Which.Message.Should().Be("cancel reserved waiter for test cleanup");
                joined.Should().BeFalse("nested Begin joins the existing automatic transaction");
                // Finish all storage cleanup and cold validation before the liveness assertion.
                engine.Dispose();
                files.WriteIsFree();
                using var cold = new LiteDatabase(files.Filename);
                Verify(cold, 0);
                cold.GetCollection("admitted").FindAll().Should().BeEquivalentTo(new[] { Row(1000, 1) });
                returnedWithoutCancellation.Should().BeTrue(
                    "a nested Begin cannot wait on a queued reservation whose WRITE admission requires this callback to return");
            }
        }
    }
}
#endif
