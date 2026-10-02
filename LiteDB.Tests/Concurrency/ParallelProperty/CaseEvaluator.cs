using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteDB.Tests.Concurrency.ParallelProperty
{
    /// <summary>
    /// Why a case failed, with the real library's side (<see cref="Observed"/>: what the database did)
    /// kept apart from the model's side (<see cref="ModelVerdict"/>: what the permitted set allowed).
    /// <see cref="Identity"/> classifies the failure without values (<c>not-permitted</c>, <c>hang</c>,
    /// <c>sequential:legacy.Commit</c>, ...); shrinking and reproduction keep it fixed.
    /// </summary>
    public sealed class CaseFailure
    {
        public CaseFailure(string identity, string observed, string modelVerdict,
            IReadOnlyList<OperationRecord> records = null, bool concurrent = false, bool timingBound = false)
        {
            this.Identity = identity;
            this.Observed = observed;
            this.ModelVerdict = modelVerdict;
            this.Records = records;
            this.Concurrent = concurrent;
            this.TimingBound = timingBound;
        }

        public string Identity { get; }
        public string Observed { get; }
        public string ModelVerdict { get; }

        /// <summary>The recorded history of a parallel case (null for sequential cases).</summary>
        public IReadOnlyList<OperationRecord> Records { get; }

        /// <summary>Operations of different threads overlapped in time.</summary>
        public bool Concurrent { get; }

        /// <summary>A timing bound took part: a lock timeout, a hang or probe deadline.</summary>
        public bool TimingBound { get; }

        public override string ToString() =>
            $"[{this.Identity}]{Environment.NewLine}" +
            $"  Real library (observed):{Environment.NewLine}{this.Observed}{Environment.NewLine}" +
            $"  Model (permitted outcomes):{Environment.NewLine}{this.ModelVerdict}";
    }

    /// <summary>
    /// Evidence that parallel cases exercised concurrency: operations whose execution overlapped an
    /// operation of another thread, transactions that the explaining order interleaves with another
    /// thread's operation, and observed lock timeouts.
    /// </summary>
    public sealed class CaseStatistics
    {
        public int Operations { get; private set; }
        public int OverlappingOperations { get; private set; }
        public int InterleavedTransactions { get; private set; }
        public int LockTimeouts { get; private set; }

        internal void AddHistory(IReadOnlyList<OperationRecord> records)
        {
            this.Operations += records.Count;
            this.LockTimeouts += records.Count(r => r.Result.Kind == OutcomeKind.Timeout);
            this.OverlappingOperations += records.Count(r => Overlaps(records, r));
        }

        internal static bool Overlaps(IReadOnlyList<OperationRecord> records, OperationRecord r) =>
            records.Any(o => o.Thread != r.Thread && o.Start < r.End && r.Start < o.End);

        internal void AddWitness(IReadOnlyList<OperationRecord> order)
        {
            var open = new Dictionary<int, bool>();
            foreach (var record in order)
            {
                if (record.Command.Op == LegacyAccess.BeginTrans && record.Result.Value == "true") open[record.Thread] = false;
                foreach (var thread in open.Keys.Where(t => t != record.Thread).ToList()) open[thread] = true;
                if ((record.Command.Op == LegacyAccess.Commit || record.Command.Op == LegacyAccess.Rollback) &&
                    open.TryGetValue(record.Thread, out var interleaved))
                {
                    if (interleaved) this.InterleavedTransactions++;
                    open.Remove(record.Thread);
                }
            }
        }

        public override string ToString() =>
            $"{this.Operations} recorded operations, {this.OverlappingOperations} overlapping another thread's, " +
            $"{this.InterleavedTransactions} transactions interleaved with another thread, {this.LockTimeouts} lock timeouts";
    }

    /// <summary>Runs one case and checks it: sequential cases step by step, parallel cases by the permitted-history check.</summary>
    public static class CaseEvaluator
    {
        public static CaseFailure Evaluate(PropertyCase propertyCase, PropertyOptions options, CaseStatistics statistics = null)
        {
            if (!propertyCase.IsParallel) return SequentialChecker.Run(propertyCase, options);

            var history = ParallelExecutor.Run(propertyCase, options);
            statistics?.AddHistory(history.Records);
            var concurrent = history.Records.Any(r => CaseStatistics.Overlaps(history.Records, r));
            var timeouts = history.Records.Any(r => r.Result.Kind == OutcomeKind.Timeout);
            var check = PermittedHistoryChecker.Check(propertyCase.InitialModel(), history.Records, history.FinalContents);

            if (history.Failure != null)
            {
                // Liveness failed; the safety verdict over what completed (stuck calls uncertain) still helps triage.
                var identity = history.Failure.Substring(0, history.Failure.IndexOf(':')).ToLowerInvariant();
                return new CaseFailure(identity, history.Failure + Environment.NewLine + history.Describe(),
                    check.Permitted
                        ? "the operations that completed (stuck ones taken as uncertain) form a permitted history"
                        : check.Diagnosis,
                    history.Records, concurrent, timingBound: true);
            }

            if (check.Permitted)
            {
                statistics?.AddWitness(check.Witness);
                return null;
            }

            var unexpected = history.Records.FirstOrDefault(r => r.Result.Kind == OutcomeKind.Unexpected);
            return new CaseFailure(
                unexpected == null ? "not-permitted" : "unexpected:" + unexpected.Command.Op + ":" + unexpected.Result.Value.Split(':')[0],
                history.Describe(),
                $"No explaining order exists (checked {check.StatesExplored} states).{Environment.NewLine}{check.Diagnosis}",
                history.Records, concurrent, timeouts);
        }
    }
}
