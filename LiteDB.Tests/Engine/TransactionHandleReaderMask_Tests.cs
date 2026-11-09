using System;
using System.Reflection;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class TransactionHandleReaderMask_Tests
    {
        private static IBsonDataReader Wrap(IBsonDataReader inner) => (IBsonDataReader)Activator.CreateInstance(
            typeof(UnboundEngine).GetNestedType("Reader", BindingFlags.NonPublic),
            BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { inner }, null);

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Every_reader_entry_masks_binding_and_ticket_and_restores_them_after_nested_calls(bool throwing)
        {
            using var engine = new LiteEngine();
            var context = new TransactionContext(engine, new object(), new object());
            var ticket = typeof(TransactionContext).GetField("_ticket", BindingFlags.Static | BindingFlags.NonPublic);
            var inner = new Probe();
            var reader = Wrap(inner);
            using (TransactionContext.Enter(context))
            using (TransactionContext.Dispatch(engine))
            {
                var expectedTicket = ticket.GetValue(null);
                inner.Check = () =>
                {
                    Assert.Null(TransactionContext.For(engine));
                    Assert.Null(ticket.GetValue(null));
                    Assert.True(TransactionContext.IsExecutingOwner(context.Lease.LockOwner));
                    using (TransactionContext.MaskBinding()) Assert.Null(TransactionContext.For(engine));
                    if (throwing) throw new ApplicationException();
                };
                Action[] calls = { () => reader.Read(), () => { _ = reader.Current; },
                    () => { _ = reader["value"]; }, () => { _ = reader.Collection; },
                    () => { _ = reader.HasValues; }, reader.Dispose };
                foreach (var call in calls)
                {
                    if (throwing) Assert.Throws<ApplicationException>(call);
                    else call();
                    Assert.Same(context, TransactionContext.For(engine));
                    Assert.Same(expectedTicket, ticket.GetValue(null));
                }
            }
            Assert.True(TransactionContext.IsThreadClear);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Initially_clear_mask_restores_both_fields_changed_by_throwing_reader(bool currentEntry)
        {
            using var engine = new LiteEngine();
            var context = new TransactionContext(engine, new object(), new object());
            var current = typeof(TransactionContext).GetField("_current", BindingFlags.Static | BindingFlags.NonPublic);
            var ticket = typeof(TransactionContext).GetField("_ticket", BindingFlags.Static | BindingFlags.NonPublic);
            object savedTicket;
            using (TransactionContext.Enter(context))
            using (TransactionContext.Dispatch(engine)) savedTicket = ticket.GetValue(null);
            Assert.True(TransactionContext.IsThreadClear);
            var inner = new Probe();
            using var reader = Wrap(inner);
            inner.Check = () =>
            {
                Assert.Null(current.GetValue(null));
                Assert.Null(ticket.GetValue(null));
                current.SetValue(null, context);
                ticket.SetValue(null, savedTicket);
                throw new ApplicationException();
            };
            try
            {
                if (currentEntry) Assert.Throws<ApplicationException>(() => reader.Current);
                else Assert.Throws<ApplicationException>(() => reader.Read());
                Assert.True(TransactionContext.IsThreadClear);
            }
            finally
            {
                inner.Check = null;
                current.SetValue(null, null);
                ticket.SetValue(null, null);
            }
        }

#if NET8_0_OR_GREATER
        [Fact]
        public void Ordinary_reader_access_allocates_no_delegates_per_row()
        {
            using var reader = Wrap(new Probe());
            for (var i = 0; i < 100; i++) { reader.Read(); _ = reader.Current; }
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++) { reader.Read(); _ = reader.Current; }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, allocated);
        }
#endif

        private sealed class Probe : IBsonDataReader
        {
            internal Action Check;
            private readonly BsonValue _value = new BsonDocument();
            public BsonValue this[string field] { get { Check?.Invoke(); return _value; } }
            public string Collection { get { Check?.Invoke(); return "rows"; } }
            public BsonValue Current { get { Check?.Invoke(); return _value; } }
            public bool HasValues { get { Check?.Invoke(); return true; } }
            public bool Read() { Check?.Invoke(); return true; }
            public void Dispose() { Check?.Invoke(); }
        }
    }
}
