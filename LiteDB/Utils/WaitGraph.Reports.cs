#if DEBUG || TESTING
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace LiteDB.Utils
{
    internal static partial class WaitGraph
    {
        // Lock orders seen so far, keyed by (first, second) resource ids, with the thread that took
        // them. Bounded: past the cap new orders are not recorded and lock-order findings stop growing.
        private const int MaxOrders = 200000;
        private const int MaxFindings = 10000;
        private static readonly ConcurrentDictionary<long, KeyValuePair<ThreadState, string>> _orders =
            new ConcurrentDictionary<long, KeyValuePair<ThreadState, string>>();
        private static readonly List<Finding> _findings = new List<Finding>();
        private static readonly Dictionary<string, Finding> _findingIndex = new Dictionary<string, Finding>(StringComparer.Ordinal);
        private static readonly HashSet<WaitRule> _failing = ParseFailing(Environment.GetEnvironmentVariable("LITEDB_WAITGRAPH_FAIL"));
        private static readonly string _reportPath = Environment.GetEnvironmentVariable("LITEDB_WAITGRAPH_REPORT");
        private static readonly object _reportLock = new object();
        private static readonly Stopwatch _contextClock = Stopwatch.StartNew();
        private static string _context;

        /// <summary>A latched finding: one rule, one cycle or order signature, within one <see cref="Context"/>.</summary>
        internal sealed class Finding
        {
            internal Finding(WaitRule rule, string signature, string text, string context, double atMs, double waitedMs)
            {
                this.Rule = rule;
                this.Signature = signature;
                this.Text = text;
                this.Context = context;
                this.AtMs = atMs;
                this.WaitedMs = waitedMs;
            }

            internal WaitRule Rule { get; }
            internal string Signature { get; }
            internal string Text { get; }
            internal string Context { get; }
            /// <summary>Milliseconds from the start of the context (the test) to the first detection.</summary>
            internal double AtMs { get; }
            /// <summary>Milliseconds the detecting thread had been waiting when it was first detected.</summary>
            internal double WaitedMs { get; }
            internal int Count { get; set; } = 1;
            internal bool Fails => IsFailing(this.Rule);

            public override string ToString() =>
                $"[{this.Rule.Id()}{(this.Fails ? ", failing" : "")}] x{this.Count} at {this.AtMs:0} ms{Environment.NewLine}{this.Text}";
        }

        /// <summary>What runs now, such as a test name; findings are attributed to it. Setting it restarts its clock.</summary>
        internal static string Context
        {
            get => _context;
            set
            {
                lock (_findings)
                {
                    _context = value;
                    _contextClock.Restart();
                }
            }
        }

        /// <summary>Every finding latched so far that nobody took, in first-seen order.</summary>
        internal static IReadOnlyList<Finding> Findings
        {
            get { lock (_findings) return _findings.ToArray(); }
        }

        /// <summary>Whether a harness fails on findings of <paramref name="rule"/>; all rules only report by default.</summary>
        internal static bool IsFailing(WaitRule rule)
        {
            lock (_failing) return _failing.Contains(rule);
        }

        /// <summary>Switch <paramref name="rule"/> between reporting and failing (a harness decision, never a throw in library code).</summary>
        internal static void SetFailing(WaitRule rule, bool failing)
        {
            lock (_failing)
            {
                if (failing) _failing.Add(rule);
                else _failing.Remove(rule);
            }
        }

        /// <summary>Test hook: remove and return the latched findings, for tests that provoke them on purpose.</summary>
        internal static IReadOnlyList<Finding> TakeFindings()
        {
            lock (_findings)
            {
                var taken = _findings.ToArray();
                _findings.Clear();
                _findingIndex.Clear();
                return taken;
            }
        }

        /// <summary>
        /// For harnesses at the end of a scenario: throw <see cref="DeadlockDetectedException"/> when
        /// one of <paramref name="findings"/> belongs to a failing rule.
        /// </summary>
        internal static void ThrowIfFailing(IEnumerable<Finding> findings)
        {
            var failing = findings.Where(x => x.Fails).ToArray();
            if (failing.Length == 0) return;
            throw new DeadlockDetectedException(string.Join(Environment.NewLine + Environment.NewLine, failing.Select(x => x.ToString())));
        }

        private static void Latch(WaitRule rule, string signature, string text, DateTime waitStarted)
        {
            Finding finding;
            lock (_findings)
            {
                var key = rule.Id() + "|" + signature + "|" + _context;
                if (_findingIndex.TryGetValue(key, out var known))
                {
                    known.Count++;
                    return;
                }
                if (_findings.Count >= MaxFindings) return;
                finding = new Finding(rule, signature, text, _context, _contextClock.Elapsed.TotalMilliseconds,
                    (DateTime.UtcNow - waitStarted).TotalMilliseconds);
                _findingIndex.Add(key, finding);
                _findings.Add(finding);
            }
            Append(finding);
        }

        /// <summary>
        /// Lock-order history: <paramref name="resource"/> taken while holding <paramref name="held"/>. An
        /// inversion counts when another thread took the opposite order; one thread alone cannot deadlock on it.
        /// </summary>
        private static void RecordOrder(ThreadState state, Resource[] held, Resource resource, string site)
        {
            foreach (var earlier in held)
            {
                if (!earlier.Ordered || ReferenceEquals(earlier, resource)) continue;
                var key = ((long)earlier.Id << 32) | (uint)resource.Id;
                var reverse = ((long)resource.Id << 32) | (uint)earlier.Id;
                var inverted = _orders.TryGetValue(reverse, out var inverse) && !ReferenceEquals(inverse.Key, state);
                if (!inverted && _orders.ContainsKey(key)) continue;
                var description = $"{earlier} then {resource} on {state}" + (site == null ? "" : " at " + site);
                if (_orders.Count < MaxOrders) _orders.TryAdd(key, new KeyValuePair<ThreadState, string>(state, description));
                if (!inverted) continue;
                var pair = string.CompareOrdinal(earlier.Kind, resource.Kind) <= 0
                    ? earlier.Kind + " / " + resource.Kind
                    : resource.Kind + " / " + earlier.Kind;
                Latch(WaitRule.LockOrder, pair, "Lock-order inversion (no active cycle):" + Environment.NewLine +
                    "  " + inverse.Value + Environment.NewLine + "  " + description, DateTime.UtcNow);
            }
        }

        private static HashSet<WaitRule> ParseFailing(string value)
        {
            var rules = new HashSet<WaitRule>();
            if (string.IsNullOrEmpty(value)) return rules;
            foreach (var part in value.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (WaitRule rule in Enum.GetValues(typeof(WaitRule)))
                    if (part == "all" || string.Equals(part, rule.Id(), StringComparison.OrdinalIgnoreCase)) rules.Add(rule);
            }
            return rules;
        }

        private static void Append(Finding finding)
        {
            if (string.IsNullOrEmpty(_reportPath)) return;
            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            var line = new StringBuilder("{");
            Field(line, "rule", finding.Rule.Id()).Append(',');
            Field(line, "failing", finding.Fails ? "true" : "false", quoted: false).Append(',');
            Field(line, "signature", finding.Signature).Append(',');
            Field(line, "context", finding.Context).Append(',');
            Field(line, "atMs", finding.AtMs.ToString("0.0", invariant), quoted: false).Append(',');
            Field(line, "waitedMs", finding.WaitedMs.ToString("0.0", invariant), quoted: false).Append(',');
            Field(line, "pid", Process.GetCurrentProcess().Id.ToString(invariant), quoted: false).Append(',');
            Field(line, "text", finding.Text).Append('}');
            try
            {
                lock (_reportLock) File.AppendAllText(_reportPath, line.AppendLine().ToString());
            }
            catch (IOException)
            {
                // The report is diagnostic; the in-memory list stays authoritative.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static StringBuilder Field(StringBuilder line, string name, string value, bool quoted = true)
        {
            line.Append('"').Append(name).Append("\":");
            if (value == null) return line.Append("null");
            if (!quoted) return line.Append(value);
            line.Append('"');
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': line.Append("\\\""); break;
                    case '\\': line.Append("\\\\"); break;
                    case '\n': line.Append("\\n"); break;
                    case '\r': line.Append("\\r"); break;
                    case '\t': line.Append("\\t"); break;
                    default:
                        if (c < ' ') line.Append("\\u").Append(((int)c).ToString("x4"));
                        else line.Append(c);
                        break;
                }
            }
            return line.Append('"');
        }
    }
}
#endif
