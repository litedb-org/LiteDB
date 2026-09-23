using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace LiteDB
{
    public partial class SharedEngine
    {
        // Leased streaming readers of this instance, per creating thread.
        private readonly Dictionary<int, int> _localReaders = new Dictionary<int, int>();
        // Thread holding an extra mutex recursion and engine use; 0 when none.
        private int _pinnedThreadId;

        private int AddLocalReader()
        {
            var thread = Environment.CurrentManagedThreadId;
            lock (_localReaders)
            {
                _localReaders.TryGetValue(thread, out var count);
                _localReaders[thread] = count + 1;
            }
            return thread;
        }

        private void RemoveLocalReader(int owner)
        {
            lock (_localReaders)
            {
                if (--_localReaders[owner] > 0) return;
                _localReaders.Remove(owner);
            }
            this.ReleaseIdlePin();
            this.CheckpointAfterLastReader();
        }

        private bool HasLocalReaders(int thread)
        {
            lock (_localReaders) return _localReaders.ContainsKey(thread);
        }

        /// <summary>
        /// Run a write while owning the mutex. A write issued by a thread that is
        /// still iterating one of this instance's leased readers keeps the engine
        /// and mutex until that thread's readers are disposed, as the pre-v13
        /// reader did. Otherwise every such write would reopen and replay a WAL
        /// that the open reader keeps growing, which is quadratic in the loop.
        /// </summary>
        private T WriteDatabase<T>(Func<T> write)
        {
            this.OpenDatabase();
            try
            {
                var thread = Environment.CurrentManagedThreadId;
                if (_pinnedThreadId == 0 && this.HasLocalReaders(thread))
                {
                    // Recursive acquisition by the owner cannot block or be abandoned.
                    _mutex.WaitOne();
                    _databaseUsers++;
                    _pinnedThreadId = thread;
                }
                return write();
            }
            finally
            {
                this.CloseDatabase();
                this.ReleaseIdlePin();
            }
        }

        /// <summary>
        /// Called while owning the mutex. A pin keeps a mutex recursion on its
        /// thread, so another thread acquiring the mutex proves the owner exited.
        /// </summary>
        private void DropAbandonedPin()
        {
            if (_pinnedThreadId == 0 || _pinnedThreadId == Environment.CurrentManagedThreadId) return;
            _pinnedThreadId = 0;
            // An abandoned explicit transaction owns the engine's cleanup.
            if (_transactionRunning) return;
            _databaseUsers = 0;
            var orphan = _engine;
            _engine = null;
            orphan?.Dispose();
        }

        /// <summary>
        /// Drop the pin once its thread has no leased readers. Only the owning
        /// thread can release its mutex recursion; a reader disposed elsewhere
        /// leaves the pin to that thread's next operation or to Dispose.
        /// </summary>
        private void ReleaseIdlePin()
        {
            var thread = Environment.CurrentManagedThreadId;
            if (_pinnedThreadId != thread || this.HasLocalReaders(thread)) return;
            _pinnedThreadId = 0;
            this.CloseDatabase();
        }

        /// <summary>
        /// Writes made while readers were leased leave a WAL that only a full
        /// checkpoint can remove. Once the last reader anywhere is gone, close an
        /// engine here so the data file alone is again the whole database, as it
        /// was after the pre-v13 reader closed its engine. Best effort: it never
        /// waits for another owner of the mutex, whose own close checkpoints.
        /// </summary>
        private void CheckpointAfterLastReader()
        {
            if (_settings.ReadOnly || !LogHasContent(_settings.Filename)) return;
            try
            {
                if (!_mutex.WaitOne(0)) return;
            }
            catch (AbandonedMutexException)
            {
                // Leave abandoned-owner recovery to the next ordinary open.
                _mutex.ReleaseMutex();
                return;
            }

            try
            {
                if (_engine != null || _transactionRunning || _readers.OldestVersion().HasValue) return;
                // Closing the engine runs its checkpoint, as every shared operation does.
                this.QueryDatabase(() => 0);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is LiteException)
            {
                // Reader disposal must not fail on this cleanup. The WAL remains
                // authoritative and the next open recovers and checkpoints it.
            }
            finally
            {
                _mutex.ReleaseMutex();
            }
        }

        private static bool LogHasContent(string filename)
        {
            var log = new FileInfo(FileHelper.GetLogFile(filename));
            return log.Exists && log.Length > 0;
        }
    }
}
