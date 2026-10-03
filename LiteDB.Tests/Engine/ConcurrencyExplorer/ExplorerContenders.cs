using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using LiteDB.Tests.Safety;
using LiteDB.Utils;

namespace LiteDB.ConcurrencyTesting
{
    /// <summary>One contender operation: its stable class, body, declared deadline, and how its outcome is judged and accounted.</summary>
    internal sealed class Contender
    {
        public string Op;
        public Action Body;
        public TimeSpan Deadline = ExplorerSchedule.LockBound;
        public Action<ExplorerSchedule.Work> Account = _ => { };
    }

    /// <summary>
    /// The three contenders of a configuration: a read (or, with an external writer, a write by the
    /// other process), the maintenance dimension's operation (a checkpoint when there is none), and a
    /// write through the access kind. Each judges its own outcome against the configuration's
    /// permitted outcomes and updates the model.
    /// </summary>
    internal static class ExplorerContenders
    {
        internal static List<Contender> For(ExplorerRun run, LiteDatabase db, int slot)
        {
            var c = run.Configuration;
            var disturbance = CallbackPauseScenario.Disturbance(c);
            var id = 4 + slot * 10;
            var list = new List<Contender>();
            if (run.External != null)
            {
                var acknowledged = false;
                list.Add(new Contender
                {
                    Op = "ExternalWrite",
                    Body = () => acknowledged = run.External.Write("other", id + 4, 80),
                    Account = work =>
                    {
                        run.Judge(work, Permit.Success);
                        Reachability.Sometimes("situation:explorer-external-writer-contended");
                        if (acknowledged) run.Model.Acknowledge("other", id + 4, 80);
                        else run.Model.Uncertain("other", id + 4, 80);
                    }
                });
            }
            else
            {
                BsonDocument sentinel = null;
                list.Add(new Contender
                {
                    Op = "Read",
                    Body = () => sentinel = db.GetCollection("sentinel").FindById(42),
                    Account = work =>
                    {
                        run.Judge(work, disturbance);
                        if (work.Ok) ExplorerModel.Require(sentinel != null && sentinel["value"] == 900, "read-sentinel", "the sentinel read returned " + sentinel);
                    }
                });
            }
            list.Add(Maintenance(run, db, id + 2, disturbance));
            list.Add(Write(run, db, "Write", id, 40, disturbance));
            return list;
        }

        /// <summary>A write through the access kind: begin, upsert, commit, all on the calling actor.</summary>
        internal static Contender Write(ExplorerRun run, LiteDatabase db, string op, int id, int value, Permit disturbance, FatalFault fault = null)
        {
            var committed = false;
            var reachedCommit = false;
            var begun = false;
            var transactional = false;
            return new Contender
            {
                Op = op,
                Body = () =>
                {
                    if (fault != null) fault.Thread = Thread.CurrentThread;
                    using (var unit = run.Access.Begin(db))
                    {
                        begun = true;
                        transactional = unit.Transactional;
                        unit.Collection("other").Upsert(ExplorerModel.Row(id, value));
                        reachedCommit = true;
                        committed = unit.Commit();
                    }
                },
                Account = work =>
                {
                    run.Judge(work, disturbance);
                    if (work.Ok && committed) run.Model.Acknowledge("other", id, value);
                    // A unit that failed before Commit rolled back; an auto-commit write or a failed Commit may have taken effect.
                    else if (!work.Ok && begun && (reachedCommit || !transactional)) run.Model.Uncertain("other", id, value);
                    if (run.Configuration.Maintenance == ExplorerMaintenance.Rebuild && ExplorerJudge.Classify(work.Failure, work.Refused) == Permit.Disposed)
                        Retry(run, db, id, value);
                }
            };
        }

        private static Contender Maintenance(ExplorerRun run, LiteDatabase db, int id, Permit disturbance)
        {
            switch (run.Configuration.Maintenance)
            {
                case ExplorerMaintenance.Close:
                    return new Contender
                    {
                        Op = "Dispose", Deadline = ExplorerSchedule.Extended,
                        Body = () => run.Dispose(db),
                        Account = work =>
                        {
                            run.Judge(work, Permit.Success);
                            Reachability.Sometimes("situation:explorer-close-while-callback-paused");
                        }
                    };
                case ExplorerMaintenance.Rebuild:
                    return new Contender
                    {
                        Op = "Rebuild", Deadline = ExplorerSchedule.Extended,
                        Body = () => db.Rebuild(),
                        Account = work =>
                        {
                            // Rebuild needs exclusivity: it may be refused (open readers) or give up its bounded wait.
                            run.Judge(work, disturbance | Permit.Refusal | Permit.LockTimeout);
                            Reachability.Sometimes("situation:explorer-rebuild-while-callback-paused");
                        }
                    };
                case ExplorerMaintenance.Fatal:
                    var fault = run.Keep(run.ArmFatal(db));
                    var write = Write(run, db, "Write.fatal", id, 60, disturbance, fault);
                    var account = write.Account;
                    write.Account = work =>
                    {
                        // A write that returned must have reached the armed WAL write; one refused or disposed first never writes.
                        run.Host.FaultReached("SimulateDiskWriteFail", fault.Injected, required: work.Ok);
                        run.Host.FaultDisposed("Write.fatal", fault.Injected, FaultDisposition.Propagated, work.Failure);
                        Reachability.Sometimes("situation:explorer-fatal-stop-while-callback-paused");
                        account(work);
                    };
                    return write;
                default:
                    return new Contender
                    {
                        Op = "Checkpoint", Deadline = ExplorerSchedule.Extended,
                        Body = () => db.Checkpoint(),
                        Account = work => run.Judge(work, disturbance)
                    };
            }
        }

        /// <summary>An operation that failed cleanly behind a rebuild (Issue2965) succeeds when retried.</summary>
        private static void Retry(ExplorerRun run, LiteDatabase db, int id, int value)
        {
            if (run.IsDisposed(db)) return;
            var retry = run.D.Invoke("Write.retry", () =>
            {
                using (var unit = run.Access.Begin(db))
                {
                    unit.Collection("other").Upsert(ExplorerModel.Row(id, value));
                    ExplorerModel.Require(unit.Commit(), "retry-commit", "the retried write reported no transaction");
                }
            });
            run.D.Complete(retry);
            run.Judge(retry, Permit.Success);
            run.Model.Acknowledge("other", id, value);
            Reachability.Sometimes("situation:explorer-write-retried-after-rebuild");
        }
    }

    /// <summary>A read-only upload source that pauses at a forced boundary (if any) and runs its callback after its first 64 KiB.</summary>
    internal sealed class PausingStream : Stream
    {
        private readonly byte[] _content;
        private readonly ExplorerSchedule.Boundary _pause;
        private readonly Action _callback;
        private int _position;
        private bool _paused;

        internal PausingStream(byte[] content, ExplorerSchedule.Boundary pause, Action callback)
        {
            _content = content;
            _pause = pause;
            _callback = callback;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (!_paused && _position >= 64 * 1024)
            {
                _paused = true;
                _pause?.Hit();
                _callback();
            }
            var read = Math.Min(Math.Min(count, 16 * 1024), _content.Length - _position);
            Buffer.BlockCopy(_content, _position, buffer, offset, read);
            _position += read;
            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _content.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
