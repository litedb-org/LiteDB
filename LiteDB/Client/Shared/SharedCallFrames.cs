using System;
using System.Collections.Generic;

namespace LiteDB.Client.Shared
{
    /// <summary>
    /// Shared-mode work executing on the current thread, innermost last: public calls and
    /// reads of readers that retain their connection's native ownership. A synchronous user
    /// callback (a lazy input sequence, ReadTransform, a custom stream) runs inside such a
    /// frame. While the frame retains the ownership, another connection to the same database
    /// cannot acquire it on this thread: the ownership ends only after the callback returns.
    /// </summary>
    internal static class SharedCallFrames
    {
        [ThreadStatic] private static List<Frame> _frames;

        private readonly struct Frame
        {
            public readonly string Namespace;
            public readonly object Connection;
            public readonly Func<bool> Retains;
            public readonly bool Teardown;

            public Frame(string ns, object connection, Func<bool> retains, bool teardown)
            {
                this.Namespace = ns;
                this.Connection = connection;
                this.Retains = retains;
                this.Teardown = teardown;
            }
        }

        /// <summary>Scope of one frame; disposing it removes the frame.</summary>
        public readonly struct Scope : IDisposable
        {
            private readonly bool _entered;

            internal Scope(bool entered) => _entered = entered;

            public void Dispose()
            {
                // Frames nest with the calls, so the innermost one is this scope's.
#if DEBUG || TESTING
                if (_entered) LiteDB.Utils.WaitGraph.Exit(_frames[_frames.Count - 1].Connection);
#endif
                if (_entered) _frames.RemoveAt(_frames.Count - 1);
            }
        }

        /// <summary>
        /// Enter a frame of <paramref name="connection"/> for the mutex named <paramref name="ns"/>.
        /// <paramref name="retains"/> runs on this thread and reports whether the frame keeps
        /// the native ownership at that moment.
        /// </summary>
        public static Scope Enter(string ns, object connection, Func<bool> retains, bool teardown = false)
        {
            var frames = _frames ?? (_frames = new List<Frame>());
            frames.Add(new Frame(ns, connection, retains, teardown));
#if DEBUG || TESTING
            // The connection executes on this thread; a teardown runs under every hold of it.
            LiteDB.Utils.WaitGraph.Enter(connection, claimsAll: teardown);
#endif
            return new Scope(true);
        }

        /// <summary>
        /// Closing a core is not ordinary same-connection recursion. It may be detached
        /// from connection state already, but callbacks must not reopen or dispose its owner.
        /// </summary>
        public static bool IsTearingDown(object connection)
        {
            var frames = _frames;
            if (frames == null) return false;
            for (var i = frames.Count - 1; i >= 0; i--)
                if (frames[i].Teardown && ReferenceEquals(frames[i].Connection, connection)) return true;
            return false;
        }

        /// <summary>
        /// True when a frame of another connection to <paramref name="ns"/> retains its native
        /// ownership on this thread, so a blocking acquisition by <paramref name="connection"/>
        /// here could never complete. Frames of <paramref name="connection"/> itself are
        /// recursion, which does not wait.
        /// </summary>
        public static bool RetainedByOther(string ns, object connection)
        {
            var frames = _frames;
            if (frames == null) return false;
            for (var i = frames.Count - 1; i >= 0; i--)
            {
                var frame = frames[i];
                if (ReferenceEquals(frame.Connection, connection) ||
                    !StringComparer.Ordinal.Equals(frame.Namespace, ns)) continue;
                if (frame.Retains()) return true;
            }
            return false;
        }
    }
}
