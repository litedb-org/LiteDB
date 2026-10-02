using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteDB.Tests.Concurrency.ParallelProperty
{
    /// <summary>
    /// One generated command. <see cref="Kind"/> names the <see cref="IAccessKind"/> that executes
    /// and models it; <see cref="Op"/> is an operation name that kind understands. The integer
    /// fields are operands; <see cref="Slot"/> is free for access kinds that need one more
    /// (for example a transaction handle number).
    /// </summary>
    public sealed class PropertyCommand
    {
        public PropertyCommand(string kind, string op, int collection = 0, int key = 0, int payload = 0, int slot = 0)
        {
            this.Kind = kind ?? throw new ArgumentNullException(nameof(kind));
            this.Op = op ?? throw new ArgumentNullException(nameof(op));
            this.Collection = collection;
            this.Key = key;
            this.Payload = payload;
            this.Slot = slot;
        }

        public string Kind { get; }
        public string Op { get; }
        public int Collection { get; }
        public int Key { get; }
        public int Payload { get; }
        public int Slot { get; }

        public override string ToString()
        {
            switch (this.Op)
            {
                case DataOperations.Insert:
                case DataOperations.Upsert:
                case DataOperations.Update:
                    return $"{this.Kind}.{this.Op}(c{this.Collection}, {this.Key}, p={this.Payload})";
                case DataOperations.Delete:
                case DataOperations.FindById:
                    return $"{this.Kind}.{this.Op}(c{this.Collection}, {this.Key})";
                case DataOperations.Count:
                case DataOperations.FindAll:
                    return $"{this.Kind}.{this.Op}(c{this.Collection})";
                default:
                    return this.Slot == 0 ? $"{this.Kind}.{this.Op}" : $"{this.Kind}.{this.Op}#{this.Slot}";
            }
        }
    }

    /// <summary>
    /// A self-contained group of commands from one access kind: every transaction it opens is
    /// completed inside the unit, so units compose in any order and the shrinker may drop one.
    /// </summary>
    public sealed class CommandUnit
    {
        public CommandUnit(string kind, IEnumerable<PropertyCommand> commands)
        {
            this.Kind = kind;
            this.Commands = commands.ToList();
        }

        public string Kind { get; }
        public IReadOnlyList<PropertyCommand> Commands { get; }

        public override string ToString() => this.Commands.Count == 1
            ? this.Commands[0].ToString()
            : "{ " + string.Join("; ", this.Commands.Select(c => c.ToString())) + " }";
    }

    public enum OutcomeKind
    {
        /// <summary>The call returned; <see cref="Observation.Value"/> is its canonical result.</summary>
        Ok,
        /// <summary>The call threw a <see cref="LiteException"/> other than a lock timeout.</summary>
        Error,
        /// <summary>The call threw a <see cref="LiteException"/> with LOCK_TIMEOUT.</summary>
        Timeout,
        /// <summary>Any other exception. The model never predicts this.</summary>
        Unexpected,
        /// <summary>
        /// The operation was interrupted before it returned (for example it never finished): it may have
        /// had its complete effect or none. Only the last operation of a thread can be uncertain.
        /// </summary>
        Uncertain
    }

    /// <summary>The observable result of one command, in a canonical, comparable form.</summary>
    public sealed class Observation : IEquatable<Observation>
    {
        private Observation(OutcomeKind kind, string value)
        {
            this.Kind = kind;
            this.Value = value ?? "";
        }

        public OutcomeKind Kind { get; }
        public string Value { get; }

        public static Observation Ok(string value) => new Observation(OutcomeKind.Ok, value);
        public static Observation Ok(bool value) => new Observation(OutcomeKind.Ok, value ? "true" : "false");
        public static Observation Ok(int value) => new Observation(OutcomeKind.Ok, value.ToString());
        public static Observation Error(int liteErrorCode) => new Observation(OutcomeKind.Error, "LiteException(" + liteErrorCode + ")");
        public static readonly Observation LockTimeout = new Observation(OutcomeKind.Timeout, "LockTimeout");
        public static readonly Observation Uncertain = new Observation(OutcomeKind.Uncertain, "no response");
        public static Observation UnexpectedException(Exception ex) => new Observation(OutcomeKind.Unexpected, ex.GetType().FullName + ": " + ex.Message);

        /// <summary>Map an exception thrown by a command to its observation.</summary>
        public static Observation FromException(Exception ex)
        {
            if (ex is LiteException lite)
            {
                return lite.ErrorCode == LiteException.LOCK_TIMEOUT ? LockTimeout : Error(lite.ErrorCode);
            }
            return UnexpectedException(ex);
        }

        public bool Equals(Observation other) => other != null && other.Kind == this.Kind && other.Value == this.Value;
        public override bool Equals(object obj) => this.Equals(obj as Observation);
        public override int GetHashCode() => ((int)this.Kind * 397) ^ this.Value.GetHashCode();
        public override string ToString() => this.Kind == OutcomeKind.Ok ? this.Value : this.Kind + ":" + this.Value;
    }

    /// <summary>
    /// One executed command in a history: which thread ran it, its position in that thread's
    /// program, the observed result and the logical clock values taken just before invocation
    /// and just after response. Clock values are unique across the history; an operation whose
    /// <see cref="End"/> is below another's <see cref="Start"/> precedes it in real time.
    /// Externally produced histories (for example by an interleaving explorer) use the same type.
    /// </summary>
    public sealed class OperationRecord
    {
        public OperationRecord(int thread, int index, PropertyCommand command, Observation result, long start, long end)
        {
            if (end <= start) throw new ArgumentException("An operation must end after it starts.", nameof(end));
            this.Thread = thread;
            this.Index = index;
            this.Command = command ?? throw new ArgumentNullException(nameof(command));
            this.Result = result ?? throw new ArgumentNullException(nameof(result));
            this.Start = start;
            this.End = end;
        }

        public int Thread { get; }
        public int Index { get; }
        public PropertyCommand Command { get; }
        public Observation Result { get; }
        public long Start { get; }
        public long End { get; }

        public override string ToString() => $"T{this.Thread}#{this.Index} [{this.Start}-{this.End}] {this.Command} -> {this.Result}";
    }
}
