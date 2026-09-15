using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1834_Tests
    {
        [Fact]
        public void Any_query_after_deferred_query_thread_handoff_does_not_poison_transaction_lock()
        {
            var source = new[]
            {
                new Alert { Id = 1, Type = 1, Pending = true },
                new Alert { Id = 2, Type = 1, Pending = false },
                new Alert { Id = 3, Type = 2, Pending = true }
            };
            var expectedAny = source.Any(x => x.Type == 1 && x.Pending);

            using var db = new LiteDatabase(new System.IO.MemoryStream());
            db.Timeout = TimeSpan.FromSeconds(1);
            var alerts = db.GetCollection<Alert>("alerts");
            alerts.Insert(source);

            // Healthy control: the report ends in Enumerable.Any, so prove both
            // the fixture and predicate before exercising a query thread handoff.
            QueryPending(alerts).Any().Should().Be(expectedAny);

            using var queryStarted = new ManualResetEventSlim();
            using var resumeOwner = new ManualResetEventSlim();
            IEnumerator<Alert> cursor = null;
            Exception ownerFailure = null;
            Exception disposeFailure = null;
            bool? observedAny = null;
            var firstRowWasPending = false;

            var owner = new Thread(() =>
            {
                try
                {
                    cursor = alerts.FindAll().GetEnumerator();
                    firstRowWasPending = cursor.MoveNext() && cursor.Current.Pending;
                    queryStarted.Set();

                    if (!resumeOwner.Wait(TimeSpan.FromSeconds(10)))
                    {
                        throw new TimeoutException("The query handoff was not released.");
                    }

                    // This matches the user-visible end of the reported stack:
                    // LiteQueryable -> MoveNext -> Enumerable.Any.
                    observedAny = QueryPending(alerts).Any();
                }
                catch (Exception ex)
                {
                    ownerFailure = ex;
                }
                finally
                {
                    queryStarted.Set();
                }
            })
            {
                IsBackground = true
            };

            var started = false;
            var joined = false;
            owner.Start();
            try
            {
                started = queryStarted.Wait(TimeSpan.FromSeconds(5));
                if (started && cursor != null)
                {
                    // Deferred enumerables routinely cross threads around await.
                    // Cleanup must not strand a ReaderWriterLockSlim read lock on
                    // the thread which first advanced the query.
                    disposeFailure = Record.Exception(cursor.Dispose);
                }
            }
            finally
            {
                resumeOwner.Set();
                joined = owner.Join(TimeSpan.FromSeconds(5));
            }

            var checkpointFailure = Record.Exception(db.Checkpoint);
            var persistedIds = alerts.FindAll().Select(x => x.Id).OrderBy(x => x).ToArray();

            using (new AssertionScope())
            {
                started.Should().BeTrue("the bounded worker must reach the handoff");
                joined.Should().BeTrue("a query-lock regression must fail rather than hang the test process");
                firstRowWasPending.Should().BeTrue("the handed-off cursor must have executed a real query");
                disposeFailure.Should().BeNull("disposing a public query enumerator must complete the handoff cleanly");
                ownerFailure.Should().BeNull("the next Any query must not hit a recursive or disposed transaction lock");
                observedAny.Should().Be(expectedAny, "LiteDB must agree with the independent in-memory LINQ oracle");
                checkpointFailure.Should().BeNull("an exclusive checkpoint independently proves no read lock was leaked");
                persistedIds.Should().Equal(source.Select(x => x.Id), "query lock handling must not alter stored rows");
            }
        }

        private static IEnumerable<Alert> QueryPending(ILiteCollection<Alert> alerts)
        {
            return alerts.Query()
                .Where(x => x.Type == 1 && x.Pending)
                .ToEnumerable();
        }

        private sealed class Alert
        {
            public int Id { get; set; }
            public int Type { get; set; }
            public bool Pending { get; set; }
        }
    }
}
