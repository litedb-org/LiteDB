using System;
using System.Collections.Generic;
using System.Threading;
using LiteDB.Utils;

namespace LiteDB.Engine
{
    internal partial class DiskService
    {
        [TeardownPath("DiskService.Dispose", TeardownDisposition.Propagated,
            "Every action runs in TryAction; collected failures are thrown as AggregateException (DiskService.Dispose.cs).")]
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            var errors = new List<Exception>();
            var delete = false;

            TryAction(() => delete = !_readOnly && _checksums.JournalBytes == 0 && _logFactory.Exists() && _logPool.Writer.Value.Length == 0, errors);
            TryAction(() =>
            {
                TeardownSteps.Before("DiskService.Dispose.data-pool");
                _dataPool.Dispose();
                TeardownSteps.After("DiskService.Dispose.data-pool");
            }, errors);
            TryAction(() =>
            {
                TeardownSteps.Before("DiskService.Dispose.log-pool");
                _logPool.Dispose();
                TeardownSteps.After("DiskService.Dispose.log-pool");
            }, errors);
            if (delete) TryAction(() =>
            {
                TeardownSteps.Before("DiskService.Dispose.delete-log");
                _logFactory.Delete();
                TeardownSteps.After("DiskService.Dispose.delete-log");
            }, errors);
            TryAction(() =>
            {
                TeardownSteps.Before("DiskService.Dispose.cache");
                _cache.Dispose();
                TeardownSteps.After("DiskService.Dispose.cache");
            }, errors);

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
