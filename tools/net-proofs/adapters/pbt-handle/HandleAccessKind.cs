using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteDB.Tests.Concurrency.ParallelProperty
{
    /// <summary>
    /// Explicit transaction handles (<c>ILiteDatabase.BeginTransaction()</c> returning
    /// <see cref="ILiteTransaction"/>), the "handle" access kind of the parallel property test. Historical
    /// adapter: it compiles only against revisions that have the handle API (capability
    /// <c>handle-api</c>). Model: <see cref="HandleModel"/>. Commands (kind <c>handle</c>):
    /// <list type="bullet">
    /// <item><c>Begin#h</c> / <c>BeginZero#h</c> (TimeSpan.Zero admission budget), optionally
    /// <c>+lend&lt;s&gt;</c>: the handle is published in shared slot s, so other threads may use it
    /// (sequential handoff; overlapping use is refused by the library).</item>
    /// <item>A data operation with <c>#h</c> runs on handle h's collection; with <c>#-s</c> on whatever
    /// handle is lent in slot s (from another thread); without a suffix it is an ordinary database call
    /// made inside a handle unit (it does not enlist).</item>
    /// <item><c>InsertBulk+cb:Op/cN/k#h</c> (with <c>LITEDB_PBT_HANDLE_CALLBACKS=1</c>): a bulk insert of
    /// (Collection, Key, Payload) on handle h whose input enumeration first runs ordinary <c>Op(cN, k)</c>.</item>
    /// <item><c>Pause</c> (a short sleep, so lent handles stay available), <c>Commit</c>, <c>Rollback</c>,
    /// <c>Dispose</c>. A completion retries refused overlaps so that the unit always ends its handle.</item>
    /// </list>
    /// Units: a block with one handle, a block with two handles on the same thread (Shared: the second
    /// begin is bounded, since a parameterless one would wait for the first forever, as documented), or a
    /// single borrowed call. Shared mode generates no handle units on thread 0 (the sequential checker's
    /// same-thread read-back would wait for the live handle forever). Set <c>LITEDB_PBT_EARLY_TIMEOUT_MS</c> to report lock timeouts faster than
    /// that bound separately (see <see cref="HandleModel"/>).
    /// </summary>
    public sealed class HandleAccessKind : IAccessKind
    {
        public const string KindName = "handle";
        public const string Begin = nameof(Begin);
        public const string BeginZero = nameof(BeginZero);
        public const string Pause = nameof(Pause);
        public const string Commit = nameof(Commit);
        public const string Rollback = nameof(Rollback);
        public const string Dispose = nameof(Dispose);
        private const string LendSuffix = "+lend";

        private static readonly string[] HandleWrites = { DataOperations.Insert, DataOperations.Upsert, DataOperations.Update, DataOperations.Delete };
        private static readonly string[] HandleReads = { DataOperations.FindById, DataOperations.Count };

        /// <summary>Lock timeouts faster than this are observed as early (0: not distinguished).</summary>
        public static readonly int EarlyTimeoutMilliseconds =
            int.TryParse(Environment.GetEnvironmentVariable("LITEDB_PBT_EARLY_TIMEOUT_MS"), out var ms) && ms > 0 ? ms : 0;

        /// <summary>Require self-wait lock timeouts to be early (needs <see cref="EarlyTimeoutMilliseconds"/>).</summary>
        public static readonly bool SelfWaitFailFast = Environment.GetEnvironmentVariable("LITEDB_PBT_SELF_WAIT_FAIL_FAST") == "1";

        /// <summary>
        /// Dense handoff generation (<c>LITEDB_PBT_HANDLE_HANDOFF=dense</c>): borrowed units make 1-3 consecutive
        /// calls on the lent handle, and blocks that lend their handle pause between operations, so that calls of
        /// different threads meet on one handle more often.
        /// </summary>
        public static readonly bool DenseHandoff = Environment.GetEnvironmentVariable("LITEDB_PBT_HANDLE_HANDOFF") == "dense";

        /// <summary>
        /// Callback generation (<c>LITEDB_PBT_HANDLE_CALLBACKS=1</c>, Direct only): some handle writes become a bulk
        /// insert whose input enumeration first runs an ordinary call on the same thread (see
        /// <see cref="HandleModel"/>). Shared is excluded: ordinary callers wait for the handle's writer mutex.
        /// </summary>
        public static readonly bool Callbacks = Environment.GetEnvironmentVariable("LITEDB_PBT_HANDLE_CALLBACKS") == "1";

        /// <summary>
        /// Require a callback's ordinary write that conflicts with the executing handle to fail early
        /// (<c>LITEDB_PBT_EXECUTING_SELF_WAIT_FAIL_FAST=1</c>, needs <see cref="EarlyTimeoutMilliseconds"/>). Rule from the
        /// API card (normative rule 3), documented only from e821ae74 on: proofs using it are recorded as tuned.
        /// </summary>
        public static readonly bool ExecutingSelfWaitFailFast = Environment.GetEnvironmentVariable("LITEDB_PBT_EXECUTING_SELF_WAIT_FAIL_FAST") == "1";

        /// <summary>
        /// With <see cref="Callbacks"/>: calls on a lent handle (another thread's, by sequential handoff) may be
        /// callback commands too (<c>LITEDB_PBT_HANDLE_LENT_CALLBACKS=1</c>).
        /// </summary>
        public static readonly bool LentCallbacks = Environment.GetEnvironmentVariable("LITEDB_PBT_HANDLE_LENT_CALLBACKS") == "1";

        private const string CallbackPrefix = "InsertBulk+cb:";

        public static bool IsCallback(string op) => op.StartsWith(CallbackPrefix, StringComparison.Ordinal);

        /// <summary>The ordinary call a callback command runs from its input enumeration (kind ordinary, same payload).</summary>
        public static PropertyCommand CallbackCommand(PropertyCommand command)
        {
            var parts = command.Op.Substring(CallbackPrefix.Length).Split('/');
            return new PropertyCommand(OrdinaryAccess.KindName, parts[0], int.Parse(parts[1].Substring(1)), int.Parse(parts[2]), command.Payload);
        }

        public string Name => KindName;

        public string Capability => "handle-api";

        public int Weight => 4;

        public static bool IsBegin(string op) => op.StartsWith(Begin, StringComparison.Ordinal);

        public static bool IsBoundedBegin(string op) => op.StartsWith(BeginZero, StringComparison.Ordinal);

        public static int LendSlot(string op)
        {
            var index = op.IndexOf(LendSuffix, StringComparison.Ordinal);
            return index < 0 ? 0 : int.Parse(op.Substring(index + LendSuffix.Length));
        }

        private static bool IsCompletion(string op) => op == Commit || op == Rollback || op == Dispose;

        public CommandUnit GenerateUnit(UnitGenerationContext context)
        {
            // Shared: a live handle owns the writer mutex, and ordinary calls of every thread wait for it (as
            // documented). The sequential checker reads back on thread 0 after every step, so thread 0 (the
            // sequential case and the parallel prefix) gets no handle units in Shared mode.
            if (context.Mode == ConnectionType.Shared && context.Thread == 0) return null;
            var random = context.Random;
            var roll = random.Next(100);
            if (context.Thread > 0 && (roll < 30 || context.Budget < 2))
            {
                if (context.Budget < 1) return null;
                var slot = -(1 + random.Next(HandleModel.MaxSlots));
                var calls = DenseHandoff ? 1 + random.Next(Math.Min(3, context.Budget)) : 1;
                return Unit(Enumerable.Range(0, calls).Select(_ => DataCommand(random, context, slot, -1)).ToList());
            }
            if (context.Budget < 2) return null;
            if (roll < 55 && context.Budget >= 5) return Unit(this.Pair(context));
            return Unit(this.Solo(context));
        }

        private List<PropertyCommand> Solo(UnitGenerationContext context)
        {
            var random = context.Random;
            var handle = context.NextId();
            var home = random.Next(context.Collections);
            var lend = context.Thread > 0 && random.Next(100) < (DenseHandoff ? 75 : 50) ? 1 + random.Next(HandleModel.MaxSlots) : 0;
            var commands = new List<PropertyCommand> { BeginCommand(handle, bounded: random.Next(100) < 15, lend) };
            var operations = random.Next(Math.Min(4, context.Budget - 2) + 1);
            for (var i = 0; i < operations; i++)
            {
                var roll = random.Next(100);
                if (lend > 0 && (roll < 20 || (DenseHandoff && i % 2 == 1)))
                    commands.Add(new PropertyCommand(KindName, Pause, payload: 1 + random.Next(3), slot: handle));
                else if (context.Mode == ConnectionType.Direct && roll < 40)
                    commands.Add(DataCommand(random, context, 0, home)); // ordinary call inside the unit
                else
                    commands.Add(DataCommand(random, context, handle, home));
            }
            commands.Add(Completion(random, handle));
            return commands;
        }

        /// <summary>Two handles on one thread: separate transactions; their collection locks conflict.</summary>
        private List<PropertyCommand> Pair(UnitGenerationContext context)
        {
            var random = context.Random;
            var first = context.NextId();
            var second = context.NextId();
            var home = random.Next(context.Collections);
            var lend = context.Thread > 0 && random.Next(100) < 30 ? 1 + random.Next(HandleModel.MaxSlots) : 0;
            var bounded = context.Mode == ConnectionType.Shared || random.Next(100) < 30;
            var commands = new List<PropertyCommand>
            {
                BeginCommand(first, bounded: false, lend),
                BeginCommand(second, bounded, 0),
            };
            var operations = 1 + random.Next(Math.Min(4, context.Budget - 4));
            for (var i = 0; i < operations; i++)
            {
                commands.Add(DataCommand(random, context, random.Next(2) == 0 ? first : second, home));
            }
            var completions = new[] { Completion(random, first), Completion(random, second) };
            if (random.Next(2) == 0) Array.Reverse(completions);
            commands.AddRange(completions);
            return commands;
        }

        private static PropertyCommand BeginCommand(int handle, bool bounded, int lend) =>
            new PropertyCommand(KindName, (bounded ? BeginZero : Begin) + (lend > 0 ? LendSuffix + lend : ""), slot: handle);

        private static PropertyCommand Completion(Random random, int handle)
        {
            var roll = random.Next(100);
            return new PropertyCommand(KindName, roll < 70 ? Commit : roll < 85 ? Rollback : Dispose, slot: handle);
        }

        /// <summary>A data command on <paramref name="slot"/> (handle h &gt; 0, lent slot -s, or 0: ordinary). No FindAll:
        /// a handle enumeration is several guarded calls, which a borrowed call could split.</summary>
        private static PropertyCommand DataCommand(Random random, UnitGenerationContext context, int slot, int home)
        {
            if (Callbacks && (slot > 0 || (slot < 0 && LentCallbacks)) && context.Mode == ConnectionType.Direct && random.Next(100) < 30)
            {
                var target = home >= 0 && random.Next(10) < 8 ? home : random.Next(context.Collections);
                var inner = random.Next(10) < 7 ? HandleWrites[random.Next(HandleWrites.Length)] : HandleReads[random.Next(HandleReads.Length)];
                var innerCollection = random.Next(10) < 7 ? target : random.Next(context.Collections);
                var callback = $"{CallbackPrefix}{inner}/c{innerCollection}/{1 + random.Next(context.Keys)}";
                return new PropertyCommand(KindName, callback, target, 1 + random.Next(context.Keys), 1 + random.Next(99), slot);
            }
            var op = random.Next(10) < 6 ? HandleWrites[random.Next(HandleWrites.Length)] : HandleReads[random.Next(HandleReads.Length)];
            var collection = home >= 0 && random.Next(10) < 8 ? home : random.Next(context.Collections);
            return new PropertyCommand(KindName, op, collection, 1 + random.Next(context.Keys), 1 + random.Next(99), slot);
        }

        private static CommandUnit Unit(IEnumerable<PropertyCommand> commands) => new CommandUnit(KindName, commands);

        private static CommandUnit Unit(PropertyCommand command) => new CommandUnit(KindName, new[] { command });

        public IEnumerable<CommandUnit> ShrinkUnit(CommandUnit unit)
        {
            var commands = unit.Commands;
            for (var i = 0; i < commands.Count; i++)
            {
                if (commands.Count == 1 || IsBegin(commands[i].Op) || IsCompletion(commands[i].Op)) continue;
                yield return Unit(commands.Where((_, index) => index != i));
            }
            var begins = commands.Where(c => IsBegin(c.Op)).ToList();
            if (begins.Count == 2)
            {
                foreach (var begin in begins)
                    yield return Unit(commands.Where(c => c.Slot != begin.Slot)); // keep one handle of the pair
            }
            for (var i = 0; i < commands.Count; i++)
            {
                if (commands[i].Op == Rollback || commands[i].Op == Dispose)
                    yield return Unit(commands.Select((c, index) => index == i ? new PropertyCommand(KindName, Commit, slot: c.Slot) : c));
            }
        }

        public Observation Execute(PropertyCommand command, ThreadContext context) =>
            HandleExecution.Execute(command, context, EarlyTimeoutMilliseconds);

        public void Apply(ModelState state, PropertyCommand command, int thread, List<ModelOutcome> outcomes) =>
            HandleModel.Apply(state, command, thread, outcomes, EarlyTimeoutMilliseconds > 0);
    }
}
