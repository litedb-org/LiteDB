using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteDB.Engine
{
    public partial class LiteEngine
    {
        /// <summary>
        /// Reject raw public engine reentry from a bound handle's callback. Composed internal
        /// calls carry a one-use dispatch ticket; anything else would silently join the handle.
        /// </summary>
        private void ValidatePublicDispatch()
        {
            var authorized = TransactionContext.ConsumeDispatch(this);
            if (!authorized && TransactionContext.For(this) != null)
                throw new TransactionCapabilityException("Raw engine reentry from a transaction callback is unsupported. Use ordinary database objects for independent work.");
        }

        /// <summary>Whether the current thread owns a legacy (or still running automatic) transaction.</summary>
        internal bool CurrentThreadHasLegacyTransaction()
        {
            // A stopped or closed engine reports its own failure, not a disposed slot.
            _state.Validate();
            return _monitor.LegacySlot.Transaction != null;
        }

        /// <summary>Throw the engine's published failure, or its disposal, if it has stopped.</summary>
        internal void ThrowIfUnavailable() => _state.Validate();

        internal bool IsReadOnly => _settings.ReadOnly;

        /// <summary>Create the explicit transaction of the handle bound on this thread.</summary>
        internal void BeginHandleTransaction()
        {
            _state.Validate();
            var transaction = _monitor.GetTransaction(true, false, out var created);
            if (!created) throw new InvalidOperationException("An explicit handle must own a new transaction.");
            transaction.ExplicitTransaction = true;
        }

        /// <summary>User collections visible to the bound handle, including ones it created.</summary>
        internal string[] GetTransactionCollectionNames()
        {
            var names = new List<string>();
            using (var reader = this.Query("$cols", new Query { Select = BsonExpression.Create("$") }))
            {
                while (reader.Read())
                    if (reader.Current["type"].AsString == "user") names.Add(reader.Current["name"].AsString);
            }
            var transaction = TransactionContext.For(this)?.Slot.Transaction;
            if (transaction != null)
                names.AddRange(transaction.Snapshots.Where(snapshot => snapshot.CollectionPage != null)
                    .Select(snapshot => snapshot.CollectionName));
            return names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }
}
