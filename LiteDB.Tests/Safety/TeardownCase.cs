using System;
using System.Collections.Generic;
using System.IO;
using LiteDB.Utils;

namespace LiteDB.Tests.Safety
{
    /// <summary>Connection mode a teardown driver runs in.</summary>
    internal enum TeardownMode
    {
        Direct,
        Shared
    }

    /// <summary>
    /// Prior state a driver builds before it invokes its teardown path. The xUnit sweep uses each
    /// driver's defaults; the <c>teardown-faults</c> fuzz target draws them at random. A driver uses
    /// the elements that apply to its path and ignores the rest.
    /// </summary>
    internal sealed class TeardownPrior
    {
        /// <summary>Committed documents written before the teardown (acknowledged in the ledger).</summary>
        public int Documents { get; set; } = 20;
        /// <summary>An explicit transaction with uncommitted writes, open on another thread.</summary>
        public bool PendingTransaction { get; set; } = true;
        /// <summary>A query reader left open (partially read) on another thread.</summary>
        public bool OpenReader { get; set; } = true;
        /// <summary>A sorted query large enough to spill its sort to the <c>-tmp</c> scratch file, left open.</summary>
        public bool SpilledSort { get; set; }
        /// <summary>A FileStorage upload committed before the teardown.</summary>
        public bool Upload { get; set; }
        /// <summary>Shared mode: another connection to the same file, open across the teardown.</summary>
        public bool Peer { get; set; }
        /// <summary>The database file is encrypted.</summary>
        public bool Encrypted { get; set; }

        public static TeardownPrior Minimal() => new TeardownPrior { Documents = 5, PendingTransaction = false, OpenReader = false };

        public override string ToString() =>
            $"docs={this.Documents};tx={On(this.PendingTransaction)};reader={On(this.OpenReader)};sort={On(this.SpilledSort)};" +
            $"upload={On(this.Upload)};peer={On(this.Peer)};enc={On(this.Encrypted)}";

        private static string On(bool value) => value ? "1" : "0";
    }

    /// <summary>
    /// One run of a driver: the context a driver builds its scenario in, and what it observed. The
    /// driver opens connections, builds the prior state, calls <see cref="Invoke(Action)"/> (or the
    /// failure-list overload) with the path's entry, registers the connections it disposed and the
    /// participants still to stop, and names the database for the scenario-end oracles.
    /// </summary>
    internal sealed class TeardownCase
    {
        private readonly List<Action> _cleanup = new List<Action>();

        public TeardownCase(TeardownScenario scenario, string directory, TeardownPrior prior)
        {
            this.Scenario = scenario;
            this.Directory = directory;
            this.Prior = prior;
        }

        public TeardownScenario Scenario { get; }
        public string Directory { get; }
        public TeardownPrior Prior { get; }
        public DurableLedger Ledger { get; } = new DurableLedger();
        public OwnershipMonitor Ownership { get; set; }

        /// <summary>The database the scenario-end oracles (Quiescent, Durable) inspect.</summary>
        public string DatabasePath { get; set; }
        public string Password => this.Prior.Encrypted ? "teardown-sweep" : null;

        /// <summary>Set false when the scenario leaves nothing a cold reopen could check.</summary>
        public bool CheckDurable { get; set; } = true;
        /// <summary>Cold reopen must let a file marked invalid rebuild itself.</summary>
        public bool ReopenWithAutoRebuild { get; set; }

        public Exception Thrown { get; private set; }
        public IEnumerable<Exception> Returned { get; private set; }
        /// <summary>The caller's own primary error, when the path ran while handling it.</summary>
        public Exception Primary { get; set; }
        public bool Invoked { get; private set; }
        public List<object> Disposed { get; } = new List<object>();
        public List<string> Violations { get; } = new List<string>();
        public IReadOnlyList<Action> Cleanup => _cleanup;

        public string File(string name) => Path.Combine(this.Directory, name);

        /// <summary>Run the path's entry inside the scenario window, recording what it threw.</summary>
        public void Invoke(Action entry)
        {
            this.Invoked = true;
            this.Scenario.OpenWindow();
            try { entry(); }
            catch (Exception error) { this.Thrown = error; }
            finally { this.Scenario.CloseWindow(); }
        }

        /// <summary>Run an entry that returns a failure list.</summary>
        public void Invoke(Func<IEnumerable<Exception>> entry) => this.Invoke(() => { this.Returned = entry(); });

        /// <summary>A participant to stop after the invocation, before the scenario-end oracles.</summary>
        public void Defer(Action stop) => _cleanup.Add(stop);

        /// <summary>A driver-specific violation: <c>&lt;kind&gt;: detail</c>.</summary>
        public void Violation(string kind, string detail) => this.Violations.Add(kind + ": " + detail);
    }
}
