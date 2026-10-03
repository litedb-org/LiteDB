using System;
using System.Reflection;
using System.Threading;
using LiteDB.Client.Shared;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// The native mutexes of a Shared connection (its database's writer mutex and turnstile), a
    /// dedicated thread that holds one of them, and a probe that checks nobody else holds one.
    /// </summary>
    internal static class SharedWaitGuardsMutexes
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        internal static Mutex MainMutex(SharedEngine engine) => (Mutex)typeof(SharedEngine).GetField("_mutex", Private).GetValue(engine);

        internal static Mutex TurnMutex(SharedEngine engine)
        {
            var turnstile = typeof(SharedEngine).GetField("_turnstile", Private).GetValue(engine);
            return (Mutex)typeof(SharedMutexTurnstile).GetField("_turn", Private).GetValue(turnstile);
        }

        /// <summary>
        /// Whether a fresh thread takes <paramref name="mutex"/> at once (then releases it). An
        /// abandoned mutex counts as not free: a thread that ended without releasing it held it.
        /// </summary>
        internal static bool FreeOnAnotherThread(Mutex mutex)
        {
            var free = false;
            var probe = new Thread(() =>
            {
                try
                {
                    if (!mutex.WaitOne(0)) return;
                    free = true;
                }
                catch (AbandonedMutexException) { }
                mutex.ReleaseMutex();
            }) { IsBackground = true };
            probe.Start();
            Assert.True(probe.Join(TimeSpan.FromSeconds(10)));
            return free;
        }

        /// <summary>A dedicated thread owning a mutex until it releases it, or ends without releasing it.</summary>
        internal sealed class Holder : IDisposable
        {
            private readonly ManualResetEventSlim _held = new ManualResetEventSlim();
            private readonly ManualResetEventSlim _end = new ManualResetEventSlim();
            private readonly Thread _thread;
            private volatile bool _abandon;

            internal Holder(Mutex mutex)
            {
                _thread = new Thread(() =>
                {
                    try { mutex.WaitOne(); }
                    catch (AbandonedMutexException) { }
                    _held.Set();
                    _end.Wait();
                    if (!_abandon) mutex.ReleaseMutex();
                }) { IsBackground = true, Name = "test mutex holder" };
                _thread.Start();
                Assert.True(_held.Wait(TimeSpan.FromSeconds(10)), "The holder never acquired the mutex.");
            }

            internal void Release() => this.End(abandon: false);

            /// <summary>End the thread while it owns the mutex: the OS abandons it.</summary>
            internal void Abandon() => this.End(abandon: true);

            private void End(bool abandon)
            {
                _abandon = abandon;
                _end.Set();
                Assert.True(_thread.Join(TimeSpan.FromSeconds(10)), "The holder thread did not end.");
            }

            public void Dispose()
            {
                _end.Set();
                _thread.Join(TimeSpan.FromSeconds(10));
                _held.Dispose();
                _end.Dispose();
            }
        }
    }
}
