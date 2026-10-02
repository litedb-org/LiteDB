using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LiteDB.Tests.Mapper;

namespace LiteDB.ConcurrencyTesting
{
    /// <summary>The public operation a lifetime-chaos node runs on its actor.</summary>
    internal enum ChaosOp { InsertInput, FindTransform, Upload, Read, Write, Checkpoint }

    /// <summary>What a node's user callback does: nothing, a nested call, Dispose of its connection, or await child nodes.</summary>
    internal enum ChaosBody { None, SameConnection, Peer, OtherFile, Dispose, Await }

    /// <summary>When the maintenance actor acts: inside a node's callback, after a delay from the start, or after every node finished.</summary>
    internal enum ChaosTrigger { Callback, Delay, Final }

    /// <summary>One operation of a dependency program, run on its own actor thread.</summary>
    internal sealed class ChaosNode
    {
        public int Index;
        public int Parent = -1;
        public int Depth;
        /// <summary>Database file index (<see cref="ExplorerRun.FileModel"/>).</summary>
        public int File;
        /// <summary>Connection slot on the file (Shared roots may use a second connection).</summary>
        public int Slot;
        public ChaosOp Op;
        /// <summary>InsertInput only: the callback runs in the input sequence's finally, while the insert tears its input down.</summary>
        public bool Teardown;
        public ChaosBody Body;
        /// <summary>Await only: start and await children one by one (else start all, then await all).</summary>
        public bool Sequential;
        /// <summary>Transactional access only: complete the unit with Commit (else Rollback).</summary>
        public bool Commit = true;
        public readonly List<int> Children = new List<int>();

        public bool HasCallback => this.Op == ChaosOp.InsertInput || this.Op == ChaosOp.FindTransform || this.Op == ChaosOp.Upload;

        public string Collection => "n" + this.Index.ToString(CultureInfo.InvariantCulture);

        public string OpName => this.Op + (this.Teardown ? ".teardown" : "") + (this.HasCallback ? ".callback" : "");

        public override string ToString() =>
            $"N{this.Index} parent={this.Parent} depth={this.Depth} file={this.File} slot={this.Slot} op={this.OpName} " +
            $"body={ExplorerConfiguration.Name(this.Body)} children=[{string.Join(",", this.Children)}] " +
            (this.Sequential ? "sequential" : "parallel") + " " + (this.Commit ? "commit" : "rollback");
    }

    /// <summary>
    /// A lifetime-chaos program: 2-6 operations on as many actor threads forming a random dependency
    /// forest (a node's callback or input sequence awaits its children's operations on other threads,
    /// nested up to depth 3), plus one maintenance action (Dispose, Rebuild or an injected fatal WAL
    /// write) at a random point. Generated from (seed, step) only, so a step regenerates exactly.
    /// Without maintenance every awaited operation is independent of its waiter: Shared children run
    /// on the next file (a Shared waiter holds its file's writer ownership; waiting on the same file
    /// is the documented callback limitation), Direct children on the same file write their own
    /// collection or run on the next file. Evidence class 2: the threads run natively.
    /// </summary>
    internal sealed class LifetimeChaosProgram : IExplorerScenario, IExplorerEvidence
    {
        internal const string ScenarioName = "lifetime-chaos";
        internal const int MaxDepth = 3;

        /// <summary>Generated only (<see cref="Generate"/>): no public constructor, so scenario discovery does not register it.</summary>
        private LifetimeChaosProgram()
        {
        }

        public string Name => ScenarioName;
        public string Description => "Random dependency DAGs over 2-6 actors with concurrent Dispose, Rebuild or fatal injection.";
        public int Variants => 1;
        public int EvidenceClass => 2;
        public string Program => string.Join(Environment.NewLine, this.Lines());

        internal int Seed { get; private set; }
        internal int Step { get; private set; }
        internal ExplorerConfiguration Configuration { get; private set; }
        internal List<ChaosNode> Nodes { get; } = new List<ChaosNode>();
        internal int MaintenanceNode { get; private set; }
        internal ChaosTrigger Trigger { get; private set; }
        internal int TriggerNode { get; private set; } = -1;
        internal int DelayMs { get; private set; }
        /// <summary>Generation choices withheld because a registered hang/crash finding covers them (counted, reported).</summary>
        internal List<string> Withheld { get; } = new List<string>();

        public string NotApplicable(ExplorerConfiguration configuration, IExplorerAccess access) => null;

        public void Run(ExplorerRun run, int variant) => new LifetimeChaosExecution(this, run).Run();

        internal ExplorerVector Vector => new ExplorerVector
        {
            Scenario = ScenarioName, Variant = this.Step, Seed = this.Seed, Configuration = this.Configuration
        };

        internal IEnumerable<string> Lines()
        {
            yield return "seed=" + this.Seed + ";step=" + this.Step + ";" + this.Configuration.Signature;
            foreach (var node in this.Nodes) yield return node.ToString();
            yield return "maintenance=" + ExplorerConfiguration.Name(this.Configuration.Maintenance) + " on N" + this.MaintenanceNode +
                " trigger=" + ExplorerConfiguration.Name(this.Trigger) +
                (this.Trigger == ChaosTrigger.Callback ? " N" + this.TriggerNode : this.Trigger == ChaosTrigger.Delay ? " " + this.DelayMs + "ms" : "");
            foreach (var withheld in this.Withheld) yield return "withheld " + withheld;
        }

        /// <summary>The program of (<paramref name="seed"/>, <paramref name="step"/>) over <paramref name="accessKinds"/>.</summary>
        internal static LifetimeChaosProgram Generate(int seed, int step, IReadOnlyList<string> accessKinds, bool includeKnown)
        {
            var random = new StableRandom(unchecked(seed * 1000003 + step * 7919 + 17));
            var program = new LifetimeChaosProgram { Seed = seed, Step = step };
            var c = program.Configuration = new ExplorerConfiguration
            {
                Mode = random.Next(2) == 0 ? ExplorerMode.Direct : ExplorerMode.Shared,
                Access = accessKinds[random.Next(accessKinds.Count)],
                Encrypted = random.Next(4) == 0
            };
            var access = ExplorerAccessKinds.Find(c.Access);
            var count = 2 + random.Next(5);
            var roots = 1 + random.Next(Math.Min(3, count));
            var ops = (ChaosOp[])Enum.GetValues(typeof(ChaosOp));
            for (var i = 0; i < count; i++)
            {
                var node = new ChaosNode { Index = i, Op = ops[random.Next(ops.Length)], Commit = random.Next(4) != 0 };
                node.Teardown = node.Op == ChaosOp.InsertInput && random.Next(3) == 0;
                var parents = program.Nodes.Where(item => item.HasCallback && item.Depth < MaxDepth).ToArray();
                if (i >= roots && parents.Length > 0)
                {
                    var parent = parents[random.Next(parents.Length)];
                    node.Parent = parent.Index;
                    node.Depth = parent.Depth + 1;
                    // Shared: a waiter holds its file's writer ownership. Direct: an Upload ancestor holds the file
                    // storage collections, so an Upload child never shares its parent's file.
                    var nextFile = random.Next(2) == 0;
                    node.File = c.Shared || nextFile || node.Op == ChaosOp.Upload ? parent.File + 1 : parent.File;
                    parent.Children.Add(i);
                }
                else node.Slot = c.Shared ? random.Next(2) : 0;
                program.Nodes.Add(node);
            }
            foreach (var node in program.Nodes.Where(item => item.HasCallback))
            {
                if (node.Children.Count > 0)
                {
                    node.Body = ChaosBody.Await;
                    node.Sequential = random.Next(2) == 0;
                    continue;
                }
                var bodies = new[] { ChaosBody.None, ChaosBody.SameConnection, ChaosBody.Peer, ChaosBody.OtherFile, ChaosBody.Dispose };
                node.Body = bodies[random.Next(bodies.Length)];
                if (node.Body == ChaosBody.Peer && !c.Shared) node.Body = ChaosBody.SameConnection;
                var known = KnownHang(program, node, access);
                if (known != null && !includeKnown)
                {
                    program.Withheld.Add("N" + node.Index + " body=" + ExplorerConfiguration.Name(node.Body) + " (" + known + ")");
                    node.Body = ChaosBody.SameConnection;
                }
            }
            var maintenances = new[] { ExplorerMaintenance.None, ExplorerMaintenance.None, ExplorerMaintenance.Close, ExplorerMaintenance.Rebuild, ExplorerMaintenance.Fatal };
            c.Maintenance = maintenances[random.Next(maintenances.Length)];
            program.MaintenanceNode = random.Next(count);
            var callbacks = program.Nodes.Where(item => item.HasCallback).ToArray();
            if (callbacks.Length > 0 && random.Next(2) == 0)
            {
                program.Trigger = ChaosTrigger.Callback;
                program.TriggerNode = callbacks[random.Next(callbacks.Length)].Index;
            }
            else
            {
                program.Trigger = ChaosTrigger.Delay;
                program.DelayMs = random.Next(40);
            }
            // Direct: a Dispose or fatal stop under an active operation is a registered crash finding; keep it after every operation.
            if (!c.Shared && (c.Maintenance == ExplorerMaintenance.Close || c.Maintenance == ExplorerMaintenance.Fatal) && !includeKnown)
            {
                program.Withheld.Add("maintenance=" + ExplorerConfiguration.Name(c.Maintenance) + " under active operations (direct-dispose-under-active-operation)");
                program.Trigger = ChaosTrigger.Final;
            }
            c.Callback = Dominant(program.Nodes.Select(node => node.Body));
            return program;
        }

        /// <summary>The registered hang/crash finding a node body would reach, or null (see <see cref="ExplorerKnownFindings"/>).</summary>
        private static string KnownHang(LifetimeChaosProgram program, ChaosNode node, IExplorerAccess access)
        {
            var c = program.Configuration;
            if (!c.Shared && node.Body == ChaosBody.Dispose) return "direct-dispose-under-active-operation";
            if (c.Shared && node.Body == ChaosBody.Peer && (access?.Transactional == true || node.Op == ChaosOp.Upload))
                return "shared-peer-call-inside-own-explicit-transaction";
            if (c.Shared && node.Body == ChaosBody.Dispose && node.Teardown && access?.Transactional != true)
                return "shared-dispose-from-input-teardown";
            return null;
        }

        /// <summary>The callback dimension a program reports: its strongest body (fingerprints of known findings use it).</summary>
        private static ExplorerCallback Dominant(IEnumerable<ChaosBody> bodies)
        {
            var set = new HashSet<ChaosBody>(bodies);
            if (set.Contains(ChaosBody.Dispose)) return ExplorerCallback.Dispose;
            if (set.Contains(ChaosBody.Peer)) return ExplorerCallback.Peer;
            if (set.Contains(ChaosBody.SameConnection)) return ExplorerCallback.SameConnection;
            if (set.Contains(ChaosBody.OtherFile)) return ExplorerCallback.OtherFile;
            return ExplorerCallback.None;
        }
    }
}
