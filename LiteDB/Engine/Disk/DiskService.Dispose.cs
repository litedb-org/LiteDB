using System;
using System.Collections.Generic;
using System.Threading;

namespace LiteDB.Engine
{
    internal partial class DiskService
    {
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            var errors = new List<Exception>();
            var delete = false;

            TryAction(() => delete = !_readOnly && _checksums.JournalBytes == 0 && _logFactory.Exists() && _logPool.Writer.Value.Length == 0, errors);
            TryAction(() => _dataPool.Dispose(), errors);
            TryAction(() => _logPool.Dispose(), errors);
            if (delete) TryAction(() => _logFactory.Delete(), errors);
            TryAction(() => _cache.Dispose(), errors);

            if (errors.Count > 0) throw new AggregateException(errors);
        }

        private static void TryDispose(IDisposable disposable)
        {
            try
            {
                disposable?.Dispose();
            }
            catch
            {
                // Constructor cleanup must preserve the initialization error
                // while still attempting every remaining resource.
            }
        }

        private static void TryAction(Action action, ICollection<Exception> errors)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        }
    }
}
