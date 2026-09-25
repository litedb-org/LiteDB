#if NET8_0_OR_GREATER
using System;
using System.Security.Cryptography;
using System.Threading;
using LiteDB.Engine;

namespace LiteDB.Client.Coordinated
{
    /// <summary>
    /// Publishes the coordinator engine's events on the status page. The structural
    /// counter starts odd ("starting") before the engine opens, because opening can
    /// recover, migrate or promote the files, and becomes even only once it is open.
    /// </summary>
    internal sealed class CoordinatorSignals : ICoordinationSignals, IDisposable
    {
        private readonly object _sync = new object();
        private readonly CoordinatorStatusPage _page;
        private readonly ManualResetEventSlim _halt = new ManualResetEventSlim(false);
        private readonly Thread _heartbeat;
        private readonly long _instance;
        private int _depth;
        private long _structural;
        private long _epoch;
        private long _resets;
        private long _version;
        private bool _stopped;

        internal CoordinatorSignals(CoordinatorStatusPage page)
        {
            _page = page;
            Span<byte> random = stackalloc byte[8];
            do
            {
                RandomNumberGenerator.Fill(random);
                _instance = BitConverter.ToInt64(random);
            }
            while (_instance == 0);
            _page.Beat();
            _heartbeat = new Thread(() =>
            {
                while (!_halt.Wait(CoordinatorStatusPage.HeartbeatPeriodMilliseconds)) _page.Beat();
            })
            { IsBackground = true, Name = "LiteDB coordinator heartbeat" };
            _heartbeat.Start();
            this.StructuralBegin();
        }

        /// <summary>Stop the heartbeat, as process death would, without touching the page otherwise.</summary>
        internal void Halt()
        {
            _halt.Set();
            if (!ReferenceEquals(Thread.CurrentThread, _heartbeat)) _heartbeat.Join();
        }

        internal long Instance => _instance;

        /// <summary>The engine is open: end the startup phase at its read version.</summary>
        internal void Started(int version) => this.StructuralEnd(version);

        public void StructuralBegin()
        {
            lock (_sync)
            {
                if (_depth++ == 0) _structural++;
                this.Publish();
            }
        }

        public void StructuralEnd(int version)
        {
            lock (_sync)
            {
                if (version >= 0) this.SetVersion(version);
                if (--_depth == 0) _structural++;
                this.Publish();
            }
        }

        public void SlotReused()
        {
            lock (_sync)
            {
                _epoch++;
                this.Publish();
            }
        }

        public void Committed(int version)
        {
            lock (_sync)
            {
                this.SetVersion(version);
                this.Publish();
            }
        }

        private void SetVersion(int version)
        {
            if (version < _version) _resets++;
            _version = version;
        }

        private void Publish()
        {
            if (_stopped) return;
            _page.Write(new CoordinatorStatus(_instance, _version, _structural, _epoch, _resets));
        }

        /// <summary>
        /// Graceful stop: publish "no coordinator" so clients stop trusting the page. A
        /// crashed coordinator leaves its last state, which stays accurate because no
        /// other process can write until a successor rewrites the page.
        /// </summary>
        public void Dispose()
        {
            lock (_sync)
            {
                if (_stopped) return;
                _page.Write(new CoordinatorStatus(0, _version, _structural | 1, _epoch, _resets));
                _stopped = true;
            }
            this.Halt();
        }
    }
}
#endif
