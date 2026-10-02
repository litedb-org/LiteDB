using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace LiteDB.Tests.Concurrency.ParallelProperty
{
    /// <summary>
    /// A live handle with its bound collections and a real-time overlap detector: every harness call on the
    /// handle counts entries and in-flight calls, so a refusal can be classified as "another call on this
    /// handle overlapped" (legitimate) or not. The harness interval of a call contains the library's, so the
    /// detector never misses a library-level overlap.
    /// </summary>
    internal sealed class HandleBox
    {
        private int _entries;
        private int _inFlight;

        public HandleBox(int id, ILiteTransaction transaction)
        {
            this.Id = id;
            this.Transaction = transaction;
        }

        public int Id { get; }
        public ILiteTransaction Transaction { get; }
        public ILiteCollection<BsonDocument>[] Collections { get; set; }

        public Observation Call(Func<Observation> action, int earlyTimeoutMilliseconds)
        {
            var entry = Interlocked.Increment(ref _entries);
            var inFlight = Interlocked.Increment(ref _inFlight);
            var watch = Stopwatch.StartNew();
            Observation result;
            Exception error = null;
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                result = null;
                error = ex;
            }
            var overlapped = inFlight > 1 || Volatile.Read(ref _entries) != entry;
            Interlocked.Decrement(ref _inFlight);
            return error == null ? result : HandleExecution.Map(error, overlapped, watch.Elapsed, earlyTimeoutMilliseconds);
        }
    }

    /// <summary>Runs handle commands against the real library (see <see cref="HandleAccessKind"/>).</summary>
    public static class HandleExecution
    {
        /// <summary>How long a completion keeps retrying refused overlaps before reporting the refusal.</summary>
        private static readonly TimeSpan CompletionRetry = TimeSpan.FromSeconds(20);

        private const int SlotKeyBase = 1000000;

        /// <summary>Observed results per command class (begin, own, lent, ordinary, completion) for campaign evidence.</summary>
        public static readonly ConcurrentDictionary<string, int> Statistics = new ConcurrentDictionary<string, int>();

        public static string DescribeStatistics() =>
            string.Join(", ", Statistics.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value));

        public static Observation Execute(PropertyCommand command, ThreadContext context, int earlyTimeoutMilliseconds)
        {
            var observation = ExecuteCore(command, context, earlyTimeoutMilliseconds);
            var value = HandleAccessKind.IsCallback(command.Op) ? observation.ToString()
                : observation.Kind == OutcomeKind.Ok && !observation.Value.StartsWith("refused", StringComparison.Ordinal) &&
                observation.Value != "absent" && observation.Value != "LockTimeout:early" ? "ok" : observation.ToString();
            if (value.Length > 60) value = value.Substring(0, 60);
            Statistics.AddOrUpdate(Class(command) + ":" + value, 1, (_, n) => n + 1);
            return observation;
        }

        private static string Class(PropertyCommand command) =>
            HandleAccessKind.IsBegin(command.Op) ? "begin"
            : HandleAccessKind.IsCallback(command.Op) ? "callback"
            : !DataOperations.IsDataOperation(command.Op) ? command.Op.ToLowerInvariant()
            : command.Slot > 0 ? "own" : command.Slot < 0 ? "lent" : "ordinary";

        private static Observation ExecuteCore(PropertyCommand command, ThreadContext context, int earlyTimeoutMilliseconds)
        {
            var op = command.Op;
            if (HandleAccessKind.IsBegin(op)) return BeginHandle(command, context);
            if (op == HandleAccessKind.Pause)
            {
                Thread.Sleep(command.Payload);
                return HandleObservations.Paused;
            }
            if (op == HandleAccessKind.Commit || op == HandleAccessKind.Rollback || op == HandleAccessKind.Dispose)
                return Complete(command, context, earlyTimeoutMilliseconds);
            if (HandleAccessKind.IsCallback(op)) return Callback(command, context, earlyTimeoutMilliseconds);
            if (!DataOperations.IsDataOperation(op)) throw new ArgumentException("Unknown handle operation: " + op);

            if (command.Slot == 0)
            {
                // Ordinary call inside a handle unit: the thread's ordinary collection, never enlisted.
                var watch = Stopwatch.StartNew();
                try
                {
                    return RunData(command, context.Collection(command.Collection));
                }
                catch (Exception ex)
                {
                    return Map(ex, false, watch.Elapsed, earlyTimeoutMilliseconds);
                }
            }

            var box = command.Slot > 0 ? Own(context, command.Slot) : Lent(context, -command.Slot);
            if (box == null) return HandleObservations.Absent;
            return box.Call(() => RunData(command, box.Collections[command.Collection]), earlyTimeoutMilliseconds);
        }

        /// <summary>
        /// Bulk insert on the handle whose input enumeration first runs an ordinary call on this thread (its
        /// failure is caught and recorded). When the statement failed before reading its input, the plain
        /// observation of that failure is returned.
        /// </summary>
        private static Observation Callback(PropertyCommand command, ThreadContext context, int earlyTimeoutMilliseconds)
        {
            var box = command.Slot > 0 ? Own(context, command.Slot) : Lent(context, -command.Slot);
            if (box == null) return HandleObservations.Absent;
            var inner = HandleAccessKind.CallbackCommand(command);
            Observation callback = null;
            Action run = () =>
            {
                var watch = Stopwatch.StartNew();
                try { callback = RunData(inner, context.Collection(inner.Collection)); }
                catch (Exception ex) { callback = Map(ex, false, watch.Elapsed, earlyTimeoutMilliseconds); }
            };
            var document = new BsonDocument { ["_id"] = command.Key, ["p"] = command.Payload };
            var result = box.Call(() => Observation.Ok(box.Collections[command.Collection].InsertBulk(Input(run, document))), earlyTimeoutMilliseconds);
            return callback == null ? result : HandleObservations.Callback(callback, result);
        }

        private static IEnumerable<BsonDocument> Input(Action callback, BsonDocument document)
        {
            callback();
            yield return document;
        }

        private static Observation BeginHandle(PropertyCommand command, ThreadContext context)
        {
            ILiteTransaction transaction;
            try
            {
                transaction = HandleAccessKind.IsBoundedBegin(command.Op)
                    ? context.Database.BeginTransaction(TimeSpan.Zero)
                    : context.Database.BeginTransaction();
            }
            catch (Exception ex)
            {
                return HandleObservations.FromException(ex, overlapped: false);
            }

            var box = new HandleBox(command.Slot, transaction);
            try
            {
                box.Collections = Enumerable.Range(0, PropertyDatabase.MaxCollections)
                    .Select(c => transaction.GetCollection(PropertyDatabase.CollectionName(c)))
                    .ToArray();
            }
            catch (Exception ex)
            {
                // Not expected: a fresh handle refused its own collections. End it so it retains nothing.
                try { transaction.Dispose(); }
                catch { }
                return Observation.UnexpectedException(ex);
            }
            context.Items[command.Slot] = box;
            var lend = HandleAccessKind.LendSlot(command.Op);
            if (lend > 0) context.SharedItems[SlotKeyBase + lend] = box;
            return HandleObservations.Begun;
        }

        private static Observation Complete(PropertyCommand command, ThreadContext context, int earlyTimeoutMilliseconds)
        {
            var box = Own(context, command.Slot);
            if (box == null) return HandleObservations.Absent;
            var watch = Stopwatch.StartNew();
            Observation result;
            try
            {
                while (true)
                {
                    result = box.Call(() => Run(command.Op, box.Transaction), earlyTimeoutMilliseconds);
                    var retry = result.Equals(HandleObservations.Overlap) || result.Equals(HandleObservations.ReadersOpen);
                    if (!retry || watch.Elapsed > CompletionRetry) break;
                    Thread.Yield();
                }
            }
            finally
            {
                // Stop lending after the completion (the model unlends at the completion's point).
                for (var slot = 1; slot <= HandleModel.MaxSlots; slot++)
                {
                    ((ICollection<KeyValuePair<int, object>>)context.SharedItems).Remove(new KeyValuePair<int, object>(SlotKeyBase + slot, box));
                }
            }
            return result;
        }

        private static Observation Run(string op, ILiteTransaction transaction)
        {
            switch (op)
            {
                case HandleAccessKind.Commit:
                    transaction.Commit();
                    break;
                case HandleAccessKind.Rollback:
                    transaction.Rollback();
                    break;
                default:
                    transaction.Dispose();
                    break;
            }
            return HandleObservations.Completion(op);
        }

        private static HandleBox Own(ThreadContext context, int handle) =>
            context.Items.TryGetValue(handle, out var item) ? item as HandleBox : null;

        private static HandleBox Lent(ThreadContext context, int slot) =>
            context.SharedItems.TryGetValue(SlotKeyBase + slot, out var item) ? item as HandleBox : null;

        /// <summary>Execute a data command; exceptions propagate to the caller's mapping.</summary>
        private static Observation RunData(PropertyCommand command, ILiteCollection<BsonDocument> collection)
        {
            var document = new BsonDocument { ["_id"] = command.Key, ["p"] = command.Payload };
            switch (command.Op)
            {
                case DataOperations.Insert:
                    return Observation.Ok(collection.Insert(document).AsInt32);
                case DataOperations.Upsert:
                    return Observation.Ok(collection.Upsert(document));
                case DataOperations.Update:
                    return Observation.Ok(collection.Update(document));
                case DataOperations.Delete:
                    return Observation.Ok(collection.Delete(command.Key));
                case DataOperations.FindById:
                    var found = collection.FindById(command.Key);
                    return Observation.Ok(found == null ? "null" : found["p"].AsInt32.ToString());
                case DataOperations.Count:
                    return Observation.Ok(collection.Count());
                default:
                    throw new ArgumentException("Not a handle data operation: " + command.Op);
            }
        }

        internal static Observation Map(Exception ex, bool overlapped, TimeSpan elapsed, int earlyTimeoutMilliseconds)
        {
            var observation = HandleObservations.FromException(ex, overlapped);
            if (earlyTimeoutMilliseconds > 0 && observation.Equals(Observation.LockTimeout) && elapsed.TotalMilliseconds < earlyTimeoutMilliseconds)
                return HandleObservations.EarlyLockTimeout;
            return observation;
        }
    }
}
