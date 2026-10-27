using System;
using System.Collections.Generic;
using System.Threading;

namespace LiteDB.Engine
{
    // Optional core owner. The default Direct/Shared paths do not install this yet.
    // Closing admission is distinct from Disposed: an in-flight sync can still fail.
    internal sealed class DirectCheckpointLifetime
    {
        private readonly LiteEngine _engine;
        private readonly EngineState _state;
        private readonly DiskService _disk;
        private readonly object _sync = new object();
        private Thread _cleanupOwner;
        private bool _complete;
        private List<Exception> _errors;
        internal AutomaticCheckpointCoordinator Worker { get; }
        internal bool IsCleanupOwner => ReferenceEquals(Volatile.Read(ref _cleanupOwner), Thread.CurrentThread);

        internal DirectCheckpointLifetime(LiteEngine engine, EngineState state, DiskService disk,
            WalIndexService wal, int checkpoint)
        {
            _engine = engine;
            _state = state;
            _disk = disk;
            Worker = new AutomaticCheckpointCoordinator(disk, wal, state, checkpoint);
            disk.CheckpointBarrier = new CheckpointBarrier();
            disk.WriteAdmission = new CheckpointWriteAdmission(checkpoint, disk.GetFileLength(FileOrigin.Log));
            disk.AutomaticCheckpoint = Worker;
            state.CheckpointLifetime = this;
        }

        internal void CompleteStop(Exception error)
        {
            if (Worker.IsWorkerThread)
            {
                // An external closer may already be joining this worker. It owns
                // cleanup in that case; never wait for it from the worker itself.
                this.Close(error);
            }
            else
            {
                // Stop can originate under a callback's header/storage locks.
                // Publication is immediate; joining/cleanup must unwind those locks.
                ThreadPool.QueueUserWorkItem(_ => this.Close(error));
            }
        }

        internal List<Exception> Close(Exception error = null, bool checkpoint = true, bool final = false)
        {
            _state.Closing = true;
            _disk.WriteAdmission.Wake();
            Worker.BeginClose();
            List<Exception> completed = null;
            lock (_sync)
            {
                if (_cleanupOwner != null)
                {
                    if (this.IsCleanupOwner || Worker.IsWorkerThread) return new List<Exception>();
                    while (!_complete) Monitor.Wait(_sync);
                    completed = _errors;
                }
                else _cleanupOwner = Thread.CurrentThread;
            }
            if (completed != null)
            {
                // Worker-owned cleanup publishes completion just before its Run
                // finally. External close must also join that final exit before
                // releasing native admission, outside the cleanup state lock.
                try { Worker.Dispose(); }
                catch { /* The cleanup owner already retained the first cause. */ }
                return completed;
            }

            var errors = new List<Exception>();
            try
            {
                // Cancel starts, then finish captured/native group I/O before
                // joining the worker and retiring any handle. No leader lock is
                // held across the worker join. Late failures still latch in State.
                try { _disk.DrainCommitGroup(); }
                catch (Exception failure) { errors.Add(failure); }
                try { Worker.Dispose(); }
                catch (Exception failure) { errors.Add(failure); }
                var cause = _state.TerminalCause ?? error;
                errors.AddRange(cause == null ? _engine.Close(checkpoint, final) : _engine.Close(cause, _state));
                cause = _state.TerminalCause ?? cause;
                if (cause != null)
                {
                    errors.RemoveAll(item => ReferenceEquals(item, cause));
                    errors.Insert(0, cause);
                }
                if (errors.Count > 1)
                    errors[0].Data["LiteDB.DirectCheckpointCleanupErrors"] = errors.GetRange(1, errors.Count - 1).ToArray();
            }
            catch (Exception cleanup) { errors.Add(cleanup); }
            finally
            {
                lock (_sync)
                {
                    _state.Disposed = true;
                    _errors = errors;
                    _complete = true;
                    Monitor.PulseAll(_sync);
                }
            }
            return errors;
        }

        internal void ThrowIfFailed()
        {
            var cause = _state.TerminalCause;
            lock (_sync)
            {
                if (_complete && _errors.Count > 0) cause = _errors[0];
            }
            if (cause != null) throw cause;
        }
    }
}
