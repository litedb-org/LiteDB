using System;
using System.Threading;

namespace LiteDB.Tests.Safety
{
    /// <summary>
    /// A scenario participant on its own thread: it runs <c>setup</c> (for example begins a transaction
    /// or opens a reader), stays parked holding what it opened, and runs <c>finish</c> once released.
    /// Its thread inherits the sweep scenario, so its teardown steps count for the case. Release and
    /// join are bounded; a participant that does not stop in time is reported, never waited for forever.
    /// </summary>
    internal sealed class TeardownParticipant
    {
        public static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);

        private readonly ManualResetEventSlim _ready = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim _release = new ManualResetEventSlim(false);
        private readonly Thread _thread;
        private Exception _setupError;

        private TeardownParticipant(string name, Action setup, Action finish, bool exitWithoutFinish)
        {
            _thread = new Thread(() =>
            {
                try { setup(); }
                catch (Exception error) { _setupError = error; }
                _ready.Set();
                if (_setupError != null || exitWithoutFinish) return;
                _release.Wait();
                try { finish?.Invoke(); }
                catch (Exception) { /* A finish after teardown may be refused; the oracles judge the outcome. */ }
            }) { IsBackground = true, Name = "teardown-sweep " + name };
        }

        public Thread Thread => _thread;

        /// <summary>Start a participant and wait until its setup completed.</summary>
        public static TeardownParticipant Start(TeardownCase c, string name, Action setup, Action finish)
        {
            var participant = new TeardownParticipant(name, setup, finish, exitWithoutFinish: false);
            participant.Begin(c);
            c.Defer(participant.Stop);
            return participant;
        }

        /// <summary>A participant whose thread exits right after setup, abandoning what it opened.</summary>
        public static TeardownParticipant Abandon(TeardownCase c, string name, Action setup)
        {
            var participant = new TeardownParticipant(name, setup, null, exitWithoutFinish: true);
            participant.Begin(c);
            return participant;
        }

        private void Begin(TeardownCase c)
        {
            _thread.Start();
            if (!_ready.Wait(Bound)) throw new TimeoutException($"Participant {_thread.Name} did not complete its setup.");
            if (_setupError != null) throw new InvalidOperationException($"Participant {_thread.Name} failed its setup.", _setupError);
        }

        /// <summary>Release the participant and join it.</summary>
        public void Stop()
        {
            _release.Set();
            if (!_thread.Join(Bound)) throw new TimeoutException($"Participant {_thread.Name} did not stop within {Bound.TotalSeconds:F0} s.");
        }
    }
}
