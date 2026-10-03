using System;
using System.Linq;
using LiteDB.Tests.Safety;

namespace LiteDB.ConcurrencyTesting
{
    /// <summary>
    /// An attributable explorer failure. <see cref="Id"/> is a stable failure id (no values, no
    /// paths): DEADLINE_&lt;OP&gt;, OWNERSHIP_&lt;KIND&gt;, CONNECTION_CLEAN_&lt;KIND&gt;,
    /// QUIESCENT_&lt;KIND&gt;, DURABLE_&lt;KIND&gt;, EXPLORER_&lt;CHECK&gt;, UNEXPECTED_EXCEPTION_&lt;TYPE&gt;.
    /// </summary>
    internal sealed class ExplorerFailure : Exception
    {
        public ExplorerFailure(string id, string message, Exception inner = null) : base(id + ": " + message, inner)
        {
            this.Id = id;
        }

        public string Id { get; }

        internal static string Safe(string value) =>
            new string((value ?? "").Select(c => char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_').ToArray());
    }

    /// <summary>
    /// Where the explorer's oracles report. The xUnit host (<see cref="ExplorerLocalHost"/>) uses the
    /// M1 probes directly; the fuzz host routes the same checks through <c>FuzzContext</c> so they
    /// write the run's evidence files. Every member runs on the thread that calls it; failures are
    /// exceptions (the explorer converts them to a failure id with <see cref="FailureId"/>).
    /// </summary>
    internal interface IExplorerHost : IDisposable
    {
        /// <summary>Runs <paramref name="call"/> inline under its declared per-operation deadline.</summary>
        void Execute(string op, string dimension, TimeSpan deadline, Action call);

        /// <summary>The first operation that missed its deadline, or null (polled by the controller).</summary>
        ExplorerFailure Overdue();

        /// <summary>Starts observing Shared core lifecycles and writer-mutex releases (before connections open).</summary>
        void WatchOwnership();

        /// <summary>Point check of the Shared ownership invariant, plus releases latched since the last call.</summary>
        void Ownership(SharedEngine connection, string point);

        void ConnectionClean(object connection, string op);

        /// <summary>Only at scenario end, after every actor stopped and every connection to <paramref name="path"/> closed.</summary>
        void Quiescent(string path, string point);

        void Durable(DurableLedger ledger, ILiteDatabase reopened, string point);

        /// <summary>The stable failure id of an exception a host or scenario raised.</summary>
        string FailureId(Exception error);

        /// <summary>FaultReached: the injected fault fired (required: a scenario that exists to inject it).</summary>
        void FaultReached(string fault, Exception injected, bool required);

        /// <summary>FaultDisposed: the call disposed of the fired fault as its path declares.</summary>
        void FaultDisposed(string op, Exception injected, FaultDisposition declared, Exception thrown);

        /// <summary>How to start an external writer process on <paramref name="path"/>; null when unsupported.</summary>
        System.Diagnostics.ProcessStartInfo ExternalWriter(string path);
    }

    /// <summary>The xUnit host: M1 probes, a local deadline watchdog, failures as <see cref="ExplorerFailure"/>.</summary>
    internal sealed class ExplorerLocalHost : IExplorerHost
    {
        private readonly DeadlineWatchdog _watchdog;
        private readonly bool _isolated;
        private OwnershipMonitor _ownership;
        private volatile ExplorerFailure _overdue;

        /// <param name="isolated">
        /// True when nothing else runs in this process, so process-wide checks (the LiteDB thread
        /// count of Quiescent) are meaningful. xUnit runs other test classes in parallel: false.
        /// </param>
        public ExplorerLocalHost(bool isolated = false)
        {
            _isolated = isolated;
            _watchdog = new DeadlineWatchdog((late, all) => _overdue = new ExplorerFailure(
                "DEADLINE_" + ExplorerFailure.Safe(late.Operation),
                $"Operation {late.Operation} ({late.Dimension}) on thread '{late.ThreadName}' did not complete or throw within " +
                $"its declared {late.Deadline.TotalSeconds:F0} s deadline; in flight: " +
                string.Join(", ", all.Select(item => $"{item.Operation} on '{item.ThreadName}' {item.ElapsedMs:F0} ms"))));
        }

        public void Execute(string op, string dimension, TimeSpan deadline, Action call)
        {
            var item = _watchdog.Begin(op, dimension, 0, deadline);
            Exception thrown = null;
            try { call(); }
            catch (Exception error)
            {
                thrown = error;
                throw;
            }
            finally
            {
                _watchdog.End(item);
                // A late completion still missed its deadline (the watchdog may not have polled yet).
                if (item.ElapsedMs > deadline.TotalMilliseconds && _overdue == null)
                    _overdue = new ExplorerFailure("DEADLINE_" + ExplorerFailure.Safe(op),
                        $"Operation {op} ({dimension}) took {item.ElapsedMs:F0} ms, beyond its {deadline.TotalSeconds:F0} s deadline" +
                        (thrown == null ? "." : $", then threw {thrown.GetType().FullName}: {thrown.Message}"));
            }
        }

        public ExplorerFailure Overdue() => _overdue;

        public void WatchOwnership() => _ownership = _ownership ?? new OwnershipMonitor();

        public void Ownership(SharedEngine connection, string point)
        {
            if (_ownership == null) return;
            if (_ownership.TryTake(out var latched))
                throw new ExplorerFailure("OWNERSHIP_" + latched.Kind, "latched before " + point + ": " + latched.Detail);
            var violation = connection == null ? null : _ownership.Evaluate(connection);
            if (violation != null) throw new ExplorerFailure("OWNERSHIP_" + violation.Kind, "at " + point + ": " + violation.Detail);
        }

        public void ConnectionClean(object connection, string op)
        {
            var result = ConnectionCleanProbe.Evaluate(connection, _ownership);
            if (!result.Clean)
                throw new ExplorerFailure("CONNECTION_CLEAN_" + Kind(result.Violations[0]),
                    $"after {op} ({result.Mode}): " + string.Join("; ", result.Violations));
        }

        public void Quiescent(string path, string point)
        {
            var result = QuiescentProbe.Evaluate(path, _isolated ? QuiescentProbe.IdleThreadCap : int.MaxValue);
            if (!result.Clean)
                throw new ExplorerFailure("QUIESCENT_" + Kind(result.Violations[0]),
                    $"at {point}: " + string.Join("; ", result.Violations));
        }

        public void Durable(DurableLedger ledger, ILiteDatabase reopened, string point)
        {
            var violations = ledger.Verify(reopened);
            if (violations.Count > 0)
                throw new ExplorerFailure("DURABLE_" + Kind(violations[0]), $"after {point}: " + string.Join("; ", violations.Take(5)));
        }

        public string FailureId(Exception error) => ExplorerRun.DefaultFailureId(error);

        public void FaultReached(string fault, Exception injected, bool required)
        {
            if (injected == null && required)
                throw new ExplorerFailure("FAULT_NOT_REACHED_" + ExplorerFailure.Safe(fault), "the scenario requires fault " + fault + " to fire");
        }

        public void FaultDisposed(string op, Exception injected, FaultDisposition declared, Exception thrown)
        {
            if (injected == null) return;
            var observed = FaultDisposedProbe.Observe(injected, thrown);
            if (observed != FaultDisposition.None && (declared & observed) == observed) return;
            var label = observed == FaultDisposition.None ? "Replaced" : observed.ToString();
            throw new ExplorerFailure("FAULT_DISPOSED_" + ExplorerFailure.Safe(op) + "_" + ExplorerFailure.Safe(label),
                $"{op} disposed of the injected {injected.GetType().FullName} as {label}; its path declares {declared}" +
                (thrown == null ? "." : $" (threw {thrown.GetType().FullName}: {thrown.Message})."));
        }

        public System.Diagnostics.ProcessStartInfo ExternalWriter(string path) => ConcurrencyTesting.ExternalWriter.HarnessStartInfo(path);

        private static string Kind(string violation) => ExplorerFailure.Safe(violation.Split(':')[0]);

        public void Dispose()
        {
            _watchdog.Dispose();
            _ownership?.Dispose();
        }
    }
}
