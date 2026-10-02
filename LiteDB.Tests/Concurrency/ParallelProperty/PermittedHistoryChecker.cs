using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace LiteDB.Tests.Concurrency.ParallelProperty
{
    /// <summary>Verdict of <see cref="PermittedHistoryChecker"/>.</summary>
    public sealed class CheckResult
    {
        internal CheckResult(bool permitted, IReadOnlyList<OperationRecord> witness, string diagnosis, int statesExplored)
        {
            this.Permitted = permitted;
            this.Witness = witness;
            this.Diagnosis = diagnosis;
            this.StatesExplored = statesExplored;
        }

        /// <summary>The history belongs to the permitted set: an explaining order exists.</summary>
        public bool Permitted { get; }

        /// <summary>The explaining order when <see cref="Permitted"/>; otherwise the longest explained prefix.</summary>
        public IReadOnlyList<OperationRecord> Witness { get; }

        /// <summary>Model side of a rejection: the deepest point reached and what the model allowed there.</summary>
        public string Diagnosis { get; }

        public int StatesExplored { get; }
    }

    /// <summary>
    /// Permitted-history check. A history (operations of several threads with results and start/end
    /// clock values, plus the committed contents read once every thread finished) is PERMITTED when
    /// some explaining order of its operations exists that keeps each thread's program order and
    /// real-time order (an operation that ended before another started stays first), gives each
    /// operation its observed result when applied to the model at its position, and ends in the
    /// observed contents. This is not a claim that the library is linearizable in general: only the
    /// outcomes listed here are permitted, and only for the operation alphabet of the registered
    /// access kinds. The permitted set (rules refer to docs/explicit-transactions.md and LiteDB/):
    /// <list type="number">
    /// <item>Ordinary access (no explicit transaction on the thread): each call is one automatic
    /// transaction (Engine/Engine/Transaction.cs:89-115). Its writes apply atomically at its
    /// position; its reads see the committed state there.</item>
    /// <item>Bound access (BeginTrans ... Commit/Rollback, per thread): one transaction per thread;
    /// a nested BeginTrans returns false and joins it (Transaction.cs:26-32; docs "nested
    /// BeginTrans returns false"). Calls of the owner thread run in it (QueryExecutor.cs:73).
    /// Writes take the collection lock and a fresh write snapshot of the latest committed state
    /// (Services/SnapShot.cs:76-88; an earlier read snapshot is replaced, TransactionService.cs:91-96)
    /// and hold the lock until completion. Reads pin a snapshot of the collection at first access and
    /// see the transaction's own writes. Other threads never see uncommitted writes. Commit publishes
    /// all of its writes at its position.</item>
    /// <item>Rejection: Commit on a thread without a transaction while another thread has an active
    /// explicit transaction throws LiteException (Engine/Engine/TransactionCompletionGuard.cs:19-27;
    /// docs "Commit throws a descriptive LiteException"); Rollback there returns false. In Shared mode
    /// a Commit/Rollback of a thread that does not own the mutex only tries it: Commit throws while
    /// an explicit transaction runs, otherwise both return false (Client/Shared/SharedEngine.cs:276-280).
    /// No other exception (ObjectDisposedException included) is permitted for this alphabet.</item>
    /// <item>Rollback: an explicit Rollback discards the transaction's writes and returns true. A
    /// failing operation inside an explicit transaction rolls it back and marks the thread
    /// (Transaction.cs:104-111; docs "A failed operation rolls back the calling thread's explicit
    /// transaction"); the thread's later calls are automatic and its next Commit/Rollback returns
    /// false (TransactionCompletionGuard.cs:19). A failing automatic call has no effect.</item>
    /// <item>Timed-out losers: LOCK_TIMEOUT is permitted only for a write whose collection lock is
    /// held by another transaction at a position in the explaining order where its wait begins
    /// (Services/LockService.cs:87). The write has no effect. Inside an explicit transaction the
    /// failure rolls that transaction back (rule 4) at a second, later position within the same
    /// call, so the transaction keeps its locks while it waits and two crossed waiters may both
    /// time out (see <see cref="ModelOutcome.Completes"/>).
    /// In Shared mode an explicit transaction keeps the connection's mutex across calls, other
    /// threads' calls wait without a bound (SharedEngine.cs:244-252, SharedMutexOwner.cs:131), so
    /// no lock timeout is permitted there.</item>
    /// <item>Uncertain outcomes: an operation interrupted before it returned (recorded with
    /// <see cref="OutcomeKind.Uncertain"/>, last of its thread) has either its complete effect at
    /// some position or no effect at all, never a partial one.</item>
    /// </list>
    /// The search is depth-first over the next operation of each thread, memoized on (positions,
    /// model state). Externally produced histories (for example from an interleaving explorer) can be
    /// checked as long as their commands belong to registered access kinds.
    /// </summary>
    public static class PermittedHistoryChecker
    {
        /// <param name="initial">Model state before the first record (its Threads must exceed every record's thread).</param>
        /// <param name="records">All operations of all threads, any order; per thread, indexes 0..n-1.</param>
        /// <param name="finalContents">Observed committed contents per collection after all threads finished,
        /// in <see cref="DataOperations.Canonical"/> form; null (or a null entry) skips that check.</param>
        /// <param name="maxStates">Search budget; exceeding it is reported as not permitted with a note.</param>
        public static CheckResult Check(ModelState initial, IReadOnlyList<OperationRecord> records,
            IReadOnlyList<string> finalContents = null, int maxStates = 1000000)
        {
            return new Search(initial, records, finalContents, maxStates).Run();
        }

        private sealed class Search
        {
            private readonly ModelState _initial;
            private readonly OperationRecord[][] _threads;
            private readonly IReadOnlyList<string> _final;
            private readonly int _maxStates;
            private readonly HashSet<string> _visited = new HashSet<string>();
            private readonly List<OperationRecord> _path = new List<OperationRecord>();
            private List<OperationRecord> _deepestPath = new List<OperationRecord>();
            private string _deepestReason = "no operation could be placed first";
            private int _explored;

            public Search(ModelState initial, IReadOnlyList<OperationRecord> records, IReadOnlyList<string> final, int maxStates)
            {
                _initial = initial;
                _final = final;
                _maxStates = maxStates;
                _threads = records.GroupBy(r => r.Thread).OrderBy(g => g.Key)
                    .Select(g => g.OrderBy(r => r.Index).ToArray()).ToArray();
                foreach (var thread in _threads)
                {
                    var id = thread[0].Thread;
                    if (id < 0 || id >= initial.Threads)
                        throw new ArgumentException($"Thread {id} is outside the model's {initial.Threads} threads.");
                    for (var i = 0; i < thread.Length; i++)
                    {
                        if (thread[i].Index != i) throw new ArgumentException($"T{id}: indexes must be 0..n-1.");
                        if (i > 0 && thread[i].Start < thread[i - 1].End) throw new ArgumentException($"T{id}#{i} overlaps its predecessor.");
                        if (thread[i].Result.Kind == OutcomeKind.Uncertain && i != thread.Length - 1)
                            throw new ArgumentException($"T{id}#{i}: only the last operation of a thread can be uncertain.");
                    }
                }
            }

            public CheckResult Run()
            {
                var positions = new int[_threads.Length];
                if (this.Visit(positions, _initial)) return new CheckResult(true, _path.ToList(), null, _explored);

                var sb = new StringBuilder();
                if (_explored > _maxStates) sb.AppendLine($"search budget of {_maxStates} states exceeded (inconclusive)");
                sb.AppendLine($"Deepest explained prefix ({_deepestPath.Count} of {_threads.Sum(t => t.Length)} operations):");
                foreach (var record in _deepestPath) sb.AppendLine("    " + record);
                sb.Append(_deepestReason);
                return new CheckResult(false, _deepestPath, sb.ToString(), _explored);
            }

            private bool Visit(int[] positions, ModelState state)
            {
                if (++_explored > _maxStates) return false;

                var remaining = false;
                var minEnd = long.MaxValue;
                for (var t = 0; t < _threads.Length; t++)
                {
                    if (positions[t] >= _threads[t].Length) continue;
                    remaining = true;
                    minEnd = Math.Min(minEnd, _threads[t][positions[t]].End);
                }
                if (!remaining) return this.FinalMatches(state);

                if (!_visited.Add(string.Join(",", positions) + "#" + state.Key())) return false;

                var candidates = new StringBuilder();
                var outcomes = new List<ModelOutcome>();
                for (var t = 0; t < _threads.Length; t++)
                {
                    if (positions[t] >= _threads[t].Length) continue;
                    var record = _threads[t][positions[t]];
                    // Real-time order: an operation that ended before this one started goes first.
                    if (record.Start > minEnd) continue;

                    outcomes.Clear();
                    AccessKinds.Get(record.Command.Kind).Apply(state, record.Command, record.Thread, outcomes);
                    candidates.AppendLine($"    next T{record.Thread}#{record.Index} {record.Command}: observed {record.Result}; model allows " +
                        (outcomes.Count == 0 ? "nothing (it waits)" : string.Join(" | ", outcomes.Select(o => o.Observation.ToString()))));

                    var uncertain = record.Result.Kind == OutcomeKind.Uncertain;
                    // An uncertain operation may also have had no effect at all.
                    if (uncertain && state.Pending(record.Thread) == 0) outcomes.Add(new ModelOutcome(record.Result, state));
                    foreach (var outcome in outcomes)
                    {
                        if (!uncertain && !outcome.Observation.Equals(record.Result)) continue;
                        // A two-point operation keeps the thread at this record until its completing point.
                        var advance = outcome.Completes ? 1 : 0;
                        positions[t] += advance;
                        if (advance == 1) _path.Add(record);
                        if (this.Visit(positions, outcome.Next)) return true;
                        if (advance == 1) _path.RemoveAt(_path.Count - 1);
                        positions[t] -= advance;
                    }
                }

                this.NoteDeadEnd(state, candidates.ToString());
                return false;
            }

            private bool FinalMatches(ModelState state)
            {
                if (_final == null) return true;
                var mismatches = Enumerable.Range(0, Math.Min(_final.Count, state.Collections))
                    .Where(c => _final[c] != null && _final[c] != state.CommittedContents(c))
                    .Select(c => $"    c{c}: observed {_final[c]}, model {state.CommittedContents(c)}")
                    .ToList();
                if (mismatches.Count == 0) return true;
                this.NoteDeadEnd(state, "Final committed contents differ:" + Environment.NewLine + string.Join(Environment.NewLine, mismatches));
                return false;
            }

            private void NoteDeadEnd(ModelState state, string reason)
            {
                if (_path.Count < _deepestPath.Count) return;
                _deepestPath = _path.ToList();
                _deepestReason = "Model state there: " + state + Environment.NewLine + reason;
            }
        }
    }
}
