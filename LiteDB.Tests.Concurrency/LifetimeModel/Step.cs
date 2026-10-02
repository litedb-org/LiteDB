using System;
using System.Collections.Generic;

namespace LiteDB.Tests.Concurrency.LifetimeModel
{
    /// <summary>
    /// One instruction of an abstract thread's program. A model writes each real code path
    /// as a C# iterator: the code between two <c>yield return</c> statements runs atomically,
    /// and every yield is a point where Coyote may switch to another thread. Every step names
    /// the source location it stands for (<c>File.cs:line Method</c>), so the explored trace
    /// reads like the real code path and a reviewer can check each transition.
    /// </summary>
    public abstract class Step
    {
        protected Step(string site)
        {
            this.Site = site ?? throw new ArgumentNullException(nameof(site));
        }

        /// <summary>The real source location this transition corresponds to.</summary>
        public string Site { get; }

        /// <summary>A scheduling point before the code that follows it.</summary>
        public static Step At(string site) => new YieldStep(site);

        /// <summary>
        /// A wait without a bound (an untimed Monitor.Wait, WaitOne(), a user callback
        /// blocking on another task). If <paramref name="ready"/> is true the thread continues
        /// in the same atomic step, as code inside the same lock would.
        /// </summary>
        public static Step Wait(string site, Func<bool> ready) => new WaitStep(site, ready, null);

        /// <summary>
        /// A bounded wait. It expires only when no thread of the model can make progress
        /// otherwise (the timeout is long compared to the work of the model), at the earliest
        /// deadline; the thread then continues with <see cref="ModelThread.TimedOut"/> set.
        /// </summary>
        public static Step TimedWait(string site, int milliseconds, Func<bool> ready) => new WaitStep(site, ready, milliseconds);
    }

    /// <summary>A plain scheduling point.</summary>
    public sealed class YieldStep : Step
    {
        internal YieldStep(string site) : base(site)
        {
        }
    }

    /// <summary>A blocking point, see <see cref="Step.Wait"/> and <see cref="Step.TimedWait"/>.</summary>
    public sealed class WaitStep : Step
    {
        internal WaitStep(string site, Func<bool> ready, int? timeoutMilliseconds) : base(site)
        {
            this.Ready = ready ?? throw new ArgumentNullException(nameof(ready));
            this.TimeoutMilliseconds = timeoutMilliseconds;
        }

        public Func<bool> Ready { get; }

        /// <summary>Null for an unbounded wait.</summary>
        public int? TimeoutMilliseconds { get; }
    }

    internal enum ThreadStatus
    {
        Runnable,
        Blocked,
        Done,
    }

    /// <summary>An abstract thread of a model: a name, its program, and its wait state.</summary>
    public sealed class ModelThread
    {
        internal ModelThread(int id, string name)
        {
            this.Id = id;
            this.Name = name;
        }

        /// <summary>Stable identity, also used as the "managed thread id" of the model.</summary>
        public int Id { get; }

        public string Name { get; }

        /// <summary>Whether the last bounded wait of this thread ended by its timeout.</summary>
        public bool TimedOut { get; internal set; }

        /// <summary>
        /// The exception the real code would be propagating now, set by a sub-program and
        /// checked by its caller (iterators cannot yield inside try/catch). Null when none.
        /// </summary>
        public string Fault { get; set; }

        /// <summary>Free slot for a model's per-thread state (thread-static fields of the real code).</summary>
        public Dictionary<string, object> Locals { get; } = new Dictionary<string, object>();

        internal IEnumerator<Step> Program { get; set; }

        internal ThreadStatus Status { get; set; }

        internal WaitStep Waiting { get; set; }

        internal long? Deadline { get; set; }

        internal string LastSite { get; set; } = "(not started)";

        public override string ToString() => this.Name;
    }

    /// <summary>A by-reference result for iterator sub-programs.</summary>
    public sealed class Ref<T>
    {
        public T Value { get; set; }
    }
}
