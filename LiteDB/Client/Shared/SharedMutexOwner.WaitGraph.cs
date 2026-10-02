using System.Diagnostics;
#if DEBUG || TESTING
using LiteDB.Utils;
#endif

namespace LiteDB.Client.Shared
{
    /// <summary>
    /// Wait-for graph hooks of <see cref="SharedMutexOwner"/>: the OS mutex (one resource per name,
    /// process-wide), this connection's ownership (its gate), a command the holder thread runs, and a
    /// posted release in flight. Calls of the conditional methods vanish outside test builds; see
    /// docs/wait-for-graph.md.
    /// </summary>
    internal sealed partial class SharedMutexOwner
    {
#if DEBUG || TESTING
        internal enum GraphWaitSite { None, Gate, ViaHolder, Handoff, Release }

        private WaitGraph.Resource _graphMutex;
        private readonly WaitGraph.Resource _graphOwnership = new WaitGraph.Resource("shared-ownership", null, WaitPrimitive.Ownership);
        private readonly WaitGraph.Resource _graphHandoff = new WaitGraph.Resource("shared-holder-command", null, WaitPrimitive.Handoff, ordered: false);
        private readonly WaitGraph.Resource _graphRelease = new WaitGraph.Resource("shared-release-in-flight", null, WaitPrimitive.Event, ordered: false);

        private WaitGraph.Resource GraphMutex => _graphMutex ?? (_graphMutex = WaitGraph.Of(_mutex, "named-mutex", WaitPrimitive.NamedMutex));

        /// <summary>This connection's ownership, for waits of the connection outside this class.</summary>
        internal WaitGraph.Resource GraphOwnership => _graphOwnership;

        /// <summary>The OS mutex, for waits of the connection outside this class.</summary>
        internal WaitGraph.Resource GraphMutexResource => this.GraphMutex;

        /// <summary>
        /// The owner whose frames execute this ownership: the connection, which enters a frame for each
        /// public call (its exited-owner callback is bound to it). A held ownership executes only there.
        /// </summary>
        internal object GraphOwner => _ownerExited?.Target as SharedEngine ?? (object)this;

        private WaitGraph.WaitScope GraphWait(GraphWaitSite site)
        {
            switch (site)
            {
                case GraphWaitSite.Gate:
                    return WaitGraph.Wait(_graphOwnership, WaitBound.Unbounded, "SharedMutexOwner.Enter (gate)", this.GraphOwner);
                case GraphWaitSite.ViaHolder:
                    // The holder blocks on the OS mutex for this thread: it is this thread's wait.
                    return WaitGraph.Wait(this.GraphMutex, WaitBound.Unbounded, "SharedMutexOwner.Enter (via holder)", this.GraphOwner, viaHandoff: true);
                case GraphWaitSite.Handoff:
                    // An acquisition is registered by its caller as a wait on the mutex itself (ViaHolder).
                    return WaitGraph.Wait(_graphHandoff, WaitBound.Unbounded, "SharedMutexOwner.Send (holder handoff)", this.GraphOwner);
                case GraphWaitSite.Release:
                    return WaitGraph.Wait(_graphRelease, WaitBound.Unbounded, "SharedMutexOwner.WaitForRelease", this.GraphOwner);
                default:
                    return default;
            }
        }
#endif

        /// <summary>Under _sync, after the calling thread became the owner.</summary>
        [Conditional("DEBUG"), Conditional("TESTING")]
        private void GraphOwned(bool direct)
        {
#if DEBUG || TESTING
            WaitGraph.Acquired(_graphOwnership, this.GraphOwner, site: "SharedMutexOwner (gate)");
            // A directly owned OS mutex can only be released by this thread.
            WaitGraph.Acquired(this.GraphMutex, this.GraphOwner, threadAffine: direct,
                site: direct ? "SharedMutexOwner (direct)" : "SharedMutexOwner (via holder)");
#endif
        }

        /// <summary>
        /// Under _sync, when the ownership ended. The mutex hold ends with it unless a scoped thread still
        /// holds the OS mutex; a posted release then no longer depends on any owner's progress.
        /// </summary>
        [Conditional("DEBUG"), Conditional("TESTING")]
        private void GraphEnded(bool mutex)
        {
#if DEBUG || TESTING
            WaitGraph.Released(_graphOwnership, this.GraphOwner);
            if (mutex) WaitGraph.Released(this.GraphMutex, this.GraphOwner);
#endif
        }

        /// <summary>One more iteration of a poll loop (the gate wait calls the exited-owner check each round).</summary>
        [Conditional("DEBUG"), Conditional("TESTING")]
        private void GraphRecheck()
        {
#if DEBUG || TESTING
            WaitGraph.Recheck();
#endif
        }

        /// <summary>On the holder, under _sync: whoever waits for a command waits for this thread meanwhile.</summary>
        [Conditional("DEBUG"), Conditional("TESTING")]
        private void GraphCommand(Command command, bool started)
        {
#if DEBUG || TESTING
            if (command == Command.None) return;
            var resource = command == Command.ReleaseAndOpenGate ? _graphRelease : _graphHandoff;
            if (started) WaitGraph.Acquired(resource, site: "SharedMutexOwner.Run " + command);
            else WaitGraph.Released(resource);
#endif
        }

        /// <summary>
        /// On the holder: the exited owner's cleanup runs on this thread, which alone ends the mutex hold
        /// and the release in flight until it finishes.
        /// </summary>
        [Conditional("DEBUG"), Conditional("TESTING")]
        private void GraphExitedOwnerCleanup(bool started)
        {
#if DEBUG || TESTING
            if (started)
            {
                this.GraphEnded(mutex: true);
                WaitGraph.Acquired(this.GraphMutex, site: "SharedMutexOwner.ReleaseExitedOwner");
                WaitGraph.Acquired(_graphRelease, site: "SharedMutexOwner.ReleaseExitedOwner");
                return;
            }
            WaitGraph.Released(this.GraphMutex);
            WaitGraph.Released(_graphRelease);
#endif
        }
    }
}
