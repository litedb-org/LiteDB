using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    public partial class LiteEngine
    {
        /// <summary>
        /// Get engine internal pragma value
        /// </summary>
        public Task<BsonValue> PragmaAsync(string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(_header.Pragmas.Get(name));
        }

        /// <summary>
        /// Set engine pragma new value (some pragmas will be affected only after realod)
        /// </summary>
        public Task<bool> PragmaAsync(string name, BsonValue value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_header.Pragmas.Get(name) == value) return Task.FromResult(false);

            if (_locker.IsInTransaction) throw LiteException.AlreadyExistsTransaction();

            // do a inside transaction to edit pragma on commit event
            return this.AutoTransactionAsync((transaction, token) =>
            {
                transaction.Pages.Commit += (h) =>
                {
                    h.Pragmas.Set(name, value, true);
                };

                return new ValueTask<bool>(true);
            }, cancellationToken);
        }
    }
}