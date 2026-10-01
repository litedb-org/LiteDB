using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using Xunit.Abstractions;

namespace LiteDB.Tests.Engine
{
    /// <summary>Public same-connection recursion is unsafe during exclusive core teardown.</summary>
    [Collection(nameof(SharedPeerCallbackCollection))]
    public class SharedSelfCloseCallback_Tests : SharedPeerCallbackFixture
    {
        private const int BigRows = 60;
        private const string Refusal = "Cannot reenter a shared connection from inside its executing core teardown.";
        private readonly ITestOutputHelper _output;
        public SharedSelfCloseCallback_Tests(ITestOutputHelper output) { _output = output; }

        public enum Close { RetainingReader, PinHolder, LastLeasedReader, ConnectionWithEngine, ConnectionCheckpoint }
        public enum Nested { Getter, Dispose }

        public static IEnumerable<object[]> Cases() =>
            from close in (Close[])Enum.GetValues(typeof(Close))
            from encrypted in new[] { false, true }
            from nested in new[] { Nested.Getter, Nested.Dispose }
            select new object[] { close, encrypted, nested };

        [Theory]
        [MemberData(nameof(Cases))]
        public void Self_call_during_core_close_cannot_wait_or_release_native_ownership(Close close, bool encrypted, Nested nested)
        {
            this.PreserveFailure(() => this.VerifyClose(close, encrypted, nested, false),
                $"close={close}; encrypted={encrypted}; nested={nested}");
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Other_database_remains_callable_during_core_close(bool encrypted)
        {
            this.PreserveFailure(() => this.VerifyClose(Close.RetainingReader, encrypted, Nested.Getter, true),
                $"other-database; encrypted={encrypted}");
        }

        private void PreserveFailure(Action action, string phase)
        {
            try { action(); }
            catch (Exception error)
            {
                // RunBounded never cleans a graph with a live blocked worker. Collect
                // these original-volume files only after the isolated test host exits.
                this.RetainFailure();
                _output.WriteLine($"Retained fixture {Path.GetDirectoryName(this.Filename)}; {phase}; {error}");
                throw;
            }
        }

        private static BsonDocument Big(int id)
        {
            var row = Row(id);
            row["payload"] = new string('p', 4000);
            return row;
        }

        private void VerifyClose(Close close, bool encrypted, Nested nested, bool otherDatabase)
        {
            this.Seed(this.Filename, encrypted);
            var data = this.Track(CallbackFile.Open(this.Filename));
            var log = this.Track(CallbackFile.Open(FileHelper.GetLogFile(this.Filename)));
            var outer = this.Track(new SharedEngine(new EngineSettings
            {
                Filename = this.Filename, Password = Password(encrypted), DataStream = data, LogStream = log,
                ReadTransform = (_, value) => value
            }));
            // The pin must end at reader retirement, not an unrelated idle timer.
            outer.PinIdleLimit = TimeSpan.FromMinutes(10);
            outer.PinHoldLimit = TimeSpan.FromMinutes(10);
            var peer = this.OpenPeer(this.Filename, encrypted, out var peerEngine);
            SharedEngine other = null;
            if (otherDatabase)
            {
                this.Seed(this.OtherFilename, encrypted);
                other = this.Track(new SharedEngine(new EngineSettings { Filename = this.OtherFilename, Password = Password(encrypted) }));
            }
            var called = 0;
            var probes = 0;
            var excludedBefore = false;
            var excludedAfter = false;
            var opensBefore = -1;
            var opensAfter = -1;
            Exception refusal = null;
            BsonValue result = null;
            Action callback = () =>
            {
                Interlocked.Increment(ref called);
                opensBefore = outer.EngineOpens;
                excludedBefore = this.NativeExcluded(outer);
                Interlocked.Increment(ref probes);
                try
                {
                    if (otherDatabase) result = other.Pragma("USER_VERSION");
                    else if (nested == Nested.Getter) result = outer.Pragma("USER_VERSION");
                    else outer.Dispose();
                }
                catch (Exception error) { refusal = error; }
                // This must run before the original stream Write/close can unwind.
                opensAfter = outer.EngineOpens;
                excludedAfter = this.NativeExcluded(outer);
                Interlocked.Increment(ref probes);
            };
            this.RunBounded(() =>
            {
                switch (close)
                {
                    case Close.RetainingReader:
                    case Close.ConnectionWithEngine:
                    {
                        var reader = outer.Query("sentinel", new Query { ForUpdate = true });
                        outer.MutexOwner.IsHeld.Should().BeTrue();
                        outer.Insert("rows", Enumerable.Range(100, BigRows).Select(Big).ToArray(), BsonAutoId.Int32);
                        data.Arm(callback);
                        if (close == Close.ConnectionWithEngine) outer.Dispose();
                        reader.Dispose();
                        break;
                    }
                    case Close.PinHolder:
                    {
                        var reader = outer.Query("rows", new Query());
                        reader.Read().Should().BeTrue();
                        outer.Insert("rows", Enumerable.Range(100, BigRows).Select(Big).ToArray(), BsonAutoId.Int32);
                        outer.Pin.Should().NotBeNull("this case must close on the native pin holder");
                        data.Arm(callback);
                        reader.Dispose();
                        break;
                    }
                    case Close.LastLeasedReader:
                    {
                        var reader = outer.Query("rows", new Query());
                        reader.Read().Should().BeTrue();
                        outer.Pin.Should().BeNull();
                        peer.GetCollection("rows").Update(Big(1)).Should().BeTrue();
                        peerEngine.MutexOwner.WaitForRelease();
                        File.Exists(FileHelper.GetLogFile(this.Filename)).Should().BeTrue();
                        data.Arm(callback);
                        reader.Dispose();
                        break;
                    }
                    case Close.ConnectionCheckpoint:
                        outer.Insert("rows", new[] { Row(100) }, BsonAutoId.Int32);
                        data.Arm(callback);
                        outer.Dispose();
                        break;
                }
            }).Should().BeNull();

            called.Should().Be(1, "the armed close checkpoint must call the stream exactly once");
            probes.Should().Be(2, "the foreign thread must probe both sides of the attempted reentry");
            opensAfter.Should().Be(opensBefore, "refusal must precede replacement-core creation");
            excludedBefore.Should().BeTrue("the fixture must start with the real native writer mutex held");
            excludedAfter.Should().BeTrue("nested disposal must not release ownership before this callback returns");
            var connectionDisposed = close == Close.ConnectionWithEngine || close == Close.ConnectionCheckpoint;
            if (otherDatabase)
            {
                refusal.Should().BeNull();
                result.AsInt32.Should().Be(0);
                other.Insert("rows", new[] { Row(9) }, BsonAutoId.Int32);
            }
            else if (connectionDisposed && nested == Nested.Dispose) refusal.Should().BeNull("repeated disposal is a no-op");
            else if (connectionDisposed) refusal.Should().BeOfType<ObjectDisposedException>();
            else refusal.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be(Refusal);

            this.RunBounded(() => peer.GetCollection("rows").Insert(Row(9))).Should().BeNull("a foreign writer must progress after teardown");
            this.CloseAll();
            var expected = new List<int> { 1, 9 };
            if (close == Close.ConnectionCheckpoint) expected.Add(100);
            else if (close != Close.LastLeasedReader) expected.AddRange(Enumerable.Range(100, BigRows));
            for (var reopen = 0; reopen < 2; reopen++)
            {
                VerifyExact(this.Filename, encrypted, expected.ToArray(),
                    id => close == Close.LastLeasedReader ? id == 1 : close != Close.ConnectionCheckpoint && id >= 100);
                if (otherDatabase) VerifyExact(this.OtherFilename, encrypted, new[] { 1, 9 }, _ => false);
            }
        }

        // Probe the native mutex itself, never owner counters, on a fresh foreign thread.
        private bool NativeExcluded(SharedEngine engine)
        {
            var acquired = false;
            this.RunBounded(() =>
            {
                try { acquired = engine.MutexOwner.Mutex.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
                finally { if (acquired) engine.MutexOwner.Mutex.ReleaseMutex(); }
            }).Should().BeNull();
            return !acquired;
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Ordinary_same_connection_getter_recursion_still_completes(bool encrypted)
        {
            this.PreserveFailure(() =>
            {
                this.Seed(this.Filename, encrypted);
                var outer = this.OpenOuter(encrypted);
                var calls = 0;
                IEnumerable<BsonDocument> Input()
                {
                    yield return Row(2);
                    outer.Pragma("USER_VERSION").AsInt32.Should().Be(0);
                    calls++;
                    yield return Row(3);
                }
                this.RunBounded(() => outer.Insert("rows", Input(), BsonAutoId.Int32)).Should().BeNull();
                calls.Should().Be(1);
                this.CloseAll();
                for (var reopen = 0; reopen < 2; reopen++) VerifyExact(this.Filename, encrypted, new[] { 1, 2, 3 }, _ => false);
            }, $"ordinary-recursion; encrypted={encrypted}");
        }

        private static void VerifyExact(string filename, bool encrypted, int[] ids, Func<int, bool> big)
        {
            VerifyCold(filename, encrypted, ids);
            using var cold = new LiteDatabase(new ConnectionString { Filename = filename, Password = Password(encrypted) });
            foreach (var id in ids)
            {
                var row = cold.GetCollection("rows").Query().Where(Query.EQ("value", id)).ToDocuments().Single();
                ((BsonValue)row).Should().Be(big(id) ? Big(id) : Row(id), "every indexed payload must match the independent expected document");
            }
            var sentinels = cold.GetCollection("sentinel").FindAll().ToArray();
            sentinels.Should().ContainSingle();
            ((BsonValue)sentinels[0]).Should().Be(Row(42));
        }

        private sealed class CallbackFile : FileStream
        {
            private Action _onWrite;
            private CallbackFile(string path) : base(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete) { }
            internal static CallbackFile Open(string path) => new CallbackFile(path);
            internal void Arm(Action callback) => Volatile.Write(ref _onWrite, callback);
            public override void Write(byte[] array, int offset, int count)
            {
                Interlocked.Exchange(ref _onWrite, null)?.Invoke();
                base.Write(array, offset, count);
            }
#if NETCOREAPP
            public override void Write(ReadOnlySpan<byte> buffer)
            {
                Interlocked.Exchange(ref _onWrite, null)?.Invoke();
                base.Write(buffer);
            }
#endif
        }
    }
}
