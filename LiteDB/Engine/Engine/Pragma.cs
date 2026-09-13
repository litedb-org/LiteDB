using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    public partial class LiteEngine
    {
        /// <summary>
        /// Get engine internal pragma value
        /// </summary>
        public BsonValue Pragma(string name)
        {
            return _header.Pragmas.Get(name);
        }

        /// <summary>
        /// Set engine pragma new value (some pragmas will be affected only after realod)
        /// </summary>
        public bool Pragma(string name, BsonValue value)
        {
            if (this.Pragma(name) == value) return false;

            if (_locker.IsInTransaction) throw LiteException.AlreadyExistsTransaction();

            // do a inside transaction to edit pragma on commit event	
            return this.AutoTransaction(transaction =>
            {
                transaction.Pages.Commit += (h) =>
                {
                    h.Pragmas.Set(name, value, true);
                };

                return true;
            });
        }
    }
}