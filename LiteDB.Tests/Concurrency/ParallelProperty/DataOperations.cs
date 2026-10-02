using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace LiteDB.Tests.Concurrency.ParallelProperty
{
    /// <summary>
    /// The collection operations every access kind can issue (documents <c>{_id: key, p: payload}</c>),
    /// their execution through <see cref="ILiteCollection{T}"/> and their model semantics.
    /// </summary>
    public static class DataOperations
    {
        public const string Insert = nameof(Insert);
        public const string Upsert = nameof(Upsert);
        public const string Update = nameof(Update);
        public const string Delete = nameof(Delete);
        public const string FindById = nameof(FindById);
        public const string Count = nameof(Count);
        public const string FindAll = nameof(FindAll);

        private static readonly string[] Writes = { Insert, Upsert, Update, Delete };
        private static readonly string[] Reads = { FindById, Count, FindAll };

        public static bool IsDataOperation(string op) => Writes.Contains(op) || Reads.Contains(op);

        public static bool IsWrite(string op) => Writes.Contains(op);

        /// <summary>A random data command; about 60% writes.</summary>
        public static PropertyCommand Generate(string kind, Random random, int collections, int keys, int preferredCollection = -1)
        {
            var op = random.Next(10) < 6 ? Writes[random.Next(Writes.Length)] : Reads[random.Next(Reads.Length)];
            var collection = preferredCollection >= 0 && random.Next(10) < 8 ? preferredCollection : random.Next(collections);
            return new PropertyCommand(kind, op, collection, 1 + random.Next(keys), 1 + random.Next(99));
        }

        /// <summary>Same operation re-labelled for another kind (used when kinds share data commands).</summary>
        public static PropertyCommand Relabel(PropertyCommand command, string kind) =>
            new PropertyCommand(kind, command.Op, command.Collection, command.Key, command.Payload, command.Slot);

        public static Observation Execute(PropertyCommand command, ILiteCollection<BsonDocument> collection)
        {
            try
            {
                switch (command.Op)
                {
                    case Insert:
                        return Observation.Ok(collection.Insert(Document(command)).AsInt32);
                    case Upsert:
                        return Observation.Ok(collection.Upsert(Document(command)));
                    case Update:
                        return Observation.Ok(collection.Update(Document(command)));
                    case Delete:
                        return Observation.Ok(collection.Delete(command.Key));
                    case FindById:
                        var found = collection.FindById(command.Key);
                        return Observation.Ok(found == null ? "null" : found["p"].AsInt32.ToString());
                    case Count:
                        return Observation.Ok(collection.Count());
                    case FindAll:
                        return Observation.Ok(Canonical(collection.FindAll()));
                    default:
                        throw new ArgumentException("Not a data operation: " + command.Op);
                }
            }
            catch (ArgumentException) when (!IsDataOperation(command.Op))
            {
                throw;
            }
            catch (Exception ex)
            {
                return Observation.FromException(ex);
            }
        }

        private static BsonDocument Document(PropertyCommand command) =>
            new BsonDocument { ["_id"] = command.Key, ["p"] = command.Payload };

        /// <summary>Canonical form of collection contents: <c>[key:payload,...]</c> by ascending key.</summary>
        public static string Canonical(IEnumerable<BsonDocument> documents)
        {
            var pairs = documents
                .Select(d => new KeyValuePair<int, int>(d["_id"].AsInt32, d["p"].AsInt32))
                .OrderBy(p => p.Key)
                .ToList();
            var sb = new StringBuilder("[");
            foreach (var pair in pairs)
            {
                if (sb.Length > 1) sb.Append(',');
                sb.Append(pair.Key).Append(':').Append(pair.Value);
            }
            return sb.Append(']').ToString();
        }

        /// <summary>
        /// Model one data command. <paramref name="owner"/> selects the transaction the command runs
        /// in (<see cref="ModelState.Transaction"/>); when it has none, the command is an automatic
        /// transaction: reads see the committed state, writes apply atomically. A write needing a
        /// collection lock held by another owner waits (no outcome) or times out (two points inside a
        /// transaction: see <see cref="ModelOutcome.Completes"/>). <paramref name="thread"/> is the
        /// model thread executing the command. A failed command
        /// inside a transaction aborts it through <paramref name="abort"/>, as the engine rolls back
        /// the transaction of a failing operation.
        /// </summary>
        public static void Apply(ModelState state, int owner, int thread, PropertyCommand command, Action<ModelState, ModelTransaction> abort, List<ModelOutcome> outcomes)
        {
            var c = command.Collection;
            var next = state.Clone();
            var transaction = next.Transaction(owner);

            if (next.Pending(thread) == TimeoutPending)
            {
                // Second point of a timed-out write: the wait ends, the failure rolls the transaction back.
                next.SetPending(thread, 0);
                if (transaction != null) abort(next, transaction);
                outcomes.Add(new ModelOutcome(Observation.LockTimeout, next));
                return;
            }

            if (!IsWrite(command.Op))
            {
                outcomes.Add(new ModelOutcome(Observation.Ok(ReadResult(next, transaction, command)), next));
                return;
            }

            if (!next.CanWrite(transaction, c))
            {
                // The engine waits for the lock up to the TIMEOUT pragma, then throws LOCK_TIMEOUT.
                // The wait starts here, where another owner holds the lock; inside a transaction the
                // rollback of the failure happens at a later point (the transaction keeps its locks
                // meanwhile), which lets two crossed waiters both time out.
                if (transaction == null)
                {
                    outcomes.Add(new ModelOutcome(Observation.LockTimeout, next));
                    return;
                }
                next.SetPending(thread, TimeoutPending);
                outcomes.Add(new ModelOutcome(Observation.LockTimeout, next, completes: false));
                return;
            }

            if (transaction != null) next.AcquireWrite(transaction, c);
            var current = next.Read(transaction, c, command.Key);
            switch (command.Op)
            {
                case Insert:
                    if (current != 0)
                    {
                        if (transaction != null) abort(next, transaction);
                        outcomes.Add(new ModelOutcome(Observation.Error(LiteException.INDEX_DUPLICATE_KEY), next));
                        return;
                    }
                    next.Write(transaction, c, command.Key, command.Payload);
                    outcomes.Add(new ModelOutcome(Observation.Ok(command.Key), next));
                    return;
                case Upsert:
                    next.Write(transaction, c, command.Key, command.Payload);
                    outcomes.Add(new ModelOutcome(Observation.Ok(current == 0), next));
                    return;
                case Update:
                    if (current != 0) next.Write(transaction, c, command.Key, command.Payload);
                    outcomes.Add(new ModelOutcome(Observation.Ok(current != 0), next));
                    return;
                case Delete:
                    if (current != 0) next.Write(transaction, c, command.Key, 0);
                    outcomes.Add(new ModelOutcome(Observation.Ok(current != 0), next));
                    return;
            }
        }

        /// <summary><see cref="ModelState.Pending"/> phase of a write waiting to time out.</summary>
        public const int TimeoutPending = 1;

        private static string ReadResult(ModelState state, ModelTransaction transaction, PropertyCommand command)
        {
            var c = command.Collection;
            switch (command.Op)
            {
                case FindById:
                    var value = state.Read(transaction, c, command.Key);
                    return value == 0 ? "null" : value.ToString();
                case Count:
                    return Enumerable.Range(1, state.Keys).Count(k => state.Read(transaction, c, k) != 0).ToString();
                case FindAll:
                    var values = new int[state.Collections * state.Keys];
                    for (var k = 1; k <= state.Keys; k++) values[c * state.Keys + k - 1] = state.Read(transaction, c, k);
                    return state.Contents(values, c);
                default:
                    throw new ArgumentException("Not a read: " + command.Op);
            }
        }
    }
}
