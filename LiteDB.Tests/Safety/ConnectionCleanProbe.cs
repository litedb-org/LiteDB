using System;
using System.Collections.Generic;
using System.Reflection;
using LiteDB.Engine;

namespace LiteDB.Tests.Safety
{
    /// <summary>What one disposed connection still owns (see <see cref="ConnectionCleanProbe"/>).</summary>
    internal sealed class ConnectionCleanResult
    {
        public string Mode { get; set; }
        public bool CoreOpen { get; set; }
        public int LiveCores { get; set; }
        public int MutexSnapshots { get; set; }
        public bool PinActive { get; set; }
        public bool OwnershipHeld { get; set; }
        public bool HolderThread { get; set; }
        /// <summary>The idle owner thread exited after the design bound (host scheduling; see <see cref="HolderExitWait"/>).</summary>
        public bool HolderLateExit { get; set; }
        public int AdmittedCalls { get; set; }
        public int DatabaseUsers { get; set; }
        /// <summary>Leased readers that legitimately outlive the connection (their lease moved with them).</summary>
        public int TransferredReaders { get; set; }
        public int OpenTransactions { get; set; }
        public bool EngineDisposed { get; set; }
        public double WaitedMs { get; set; }
        public string[] Violations { get; set; } = new string[0];
        public bool Clean => this.Violations.Length == 0;
    }

    /// <summary>
    /// ConnectionClean: after a connection's Dispose returned, everything that connection owned
    /// is retired, released or validly transferred. Direct (LiteEngine): the engine is disposed
    /// and its transaction registry is empty. Shared (SharedEngine): no core of it is open or
    /// still closing, no mutex snapshot, pin, mutex ownership, admitted call or engine user
    /// remains, and its mutex owner thread exits by the owner's own idle rule (<see cref="HolderExitWait"/>).
    /// Leased readers may outlive the connection with their lease (counted, not failed). Other
    /// connections to the same file are not judged here (see <see cref="QuiescentProbe"/>).
    /// </summary>
    internal static class ConnectionCleanProbe
    {
        private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

        /// <summary>The engine behind a LiteDatabase (or the engine itself).</summary>
        public static ILiteEngine EngineOf(object connection) => connection as ILiteEngine ??
            connection?.GetType().GetField("_engine", Private)?.GetValue(connection) as ILiteEngine;

        public static ConnectionCleanResult Evaluate(object connection, OwnershipMonitor ownership = null)
        {
            var engine = EngineOf(connection) ?? throw new ArgumentException("Not a LiteDB connection or engine.", nameof(connection));
            var violations = new List<string>();
            ConnectionCleanResult result;
            if (engine is SharedEngine shared) result = Shared(shared, ownership, violations);
            else if (engine is LiteEngine direct) result = Direct(direct, violations);
            else throw new NotSupportedException($"ConnectionClean does not know {engine.GetType().Name}.");
            result.Violations = violations.ToArray();
            return result;
        }

        private static ConnectionCleanResult Direct(LiteEngine engine, List<string> violations)
        {
            var state = Read<EngineState>(engine, typeof(LiteEngine), "_state");
            var monitor = Read<TransactionMonitor>(engine, typeof(LiteEngine), "_monitor");
            var result = new ConnectionCleanResult
            {
                Mode = "direct", EngineDisposed = state?.Disposed ?? true,
                OpenTransactions = monitor?.Transactions.Count ?? 0
            };
            if (!result.EngineDisposed) violations.Add("engine: the Direct engine is not disposed");
            if (result.OpenTransactions > 0) violations.Add($"transactions: {result.OpenTransactions} transaction(s) still registered");
            return result;
        }

        private static ConnectionCleanResult Shared(SharedEngine engine, OwnershipMonitor ownership, List<string> violations)
        {
            var type = typeof(SharedEngine);
            var result = new ConnectionCleanResult { Mode = "shared" };
            // The owner thread exits on its own once idle; everything else is released by Dispose itself.
            var holder = HolderExitWait.Wait(engine.MutexOwner);
            result.WaitedMs = holder.WaitedMs;
            result.HolderThread = holder.Alive;
            result.HolderLateExit = holder.LateIdleExit;
            result.CoreOpen = Read<object>(engine, type, "_engine") != null;
            result.MutexSnapshots = Count(Read<object>(engine, type, "_mutexSnapshots"));
            result.PinActive = engine.Pin != null;
            result.OwnershipHeld = engine.MutexOwner.IsHeld;
            result.AdmittedCalls = Read<int>(engine, type, "_admittedCalls");
            result.DatabaseUsers = Read<int>(engine, type, "_databaseUsers");
            result.TransferredReaders = Count(Read<object>(engine, type, "_localReaders"));
            result.LiveCores = ownership?.LiveCores(engine) ?? 0;
            if (result.CoreOpen) violations.Add("cores: the connection's operation core is still attached");
            if (result.LiveCores > 0) violations.Add($"cores: {result.LiveCores} protected core(s) not closed");
            if (result.MutexSnapshots > 0) violations.Add($"cores: {result.MutexSnapshots} snapshot(s) still stream under the mutex");
            if (result.PinActive) violations.Add("pins: a pin is still published");
            if (result.OwnershipHeld) violations.Add("ownership: the connection still owns the writer mutex");
            if (holder.Violation != null) violations.Add(holder.Violation);
            if (result.AdmittedCalls > 0) violations.Add($"admissions: {result.AdmittedCalls} admitted call(s) remain");
            if (result.DatabaseUsers > 0) violations.Add($"admissions: {result.DatabaseUsers} engine user(s) remain");
            return result;
        }

        private static T Read<T>(object instance, Type type, string name)
        {
            var field = type.GetField(name, Private)
                ?? throw new InvalidOperationException($"{type.Name}.{name} no longer exists; update ConnectionCleanProbe.");
            return (T)field.GetValue(instance);
        }

        private static int Count(object collection) =>
            collection == null ? 0 : (int)collection.GetType().GetProperty("Count").GetValue(collection);
    }
}
