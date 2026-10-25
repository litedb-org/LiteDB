using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2814Backoff_Tests
    {
        private const int InitialDelayMilliseconds = 50;
        private const int MaxDelayMilliseconds = 1000;

        [Fact]
        public async Task Commits_under_a_long_lived_reader_do_not_each_wait_for_checkpoint_admission()
        {
            using var file = new TempFile();
            using var engine = new LiteEngine(file.Filename);
            engine.Pragma(Pragmas.CHECKPOINT, 1);
            var clock = new FakeClock();
            var backoff = GetBackoff(engine);
            backoff.Timestamp = clock.Read;
            var before = backoff.WaitingAttempts;
            int Attempts() => backoff.WaitingAttempts - before;
            var id = 0;
            void Commit() => engine.Insert("rows", new[] { new BsonDocument { ["_id"] = ++id } }, BsonAutoId.Int32);

            using (var reader = HeldReader.Open(engine))
            {
                for (var i = 0; i < 50; i++) Commit();
                Attempts().Should().Be(1, "only the first commit may queue behind the held reader");

                clock.Advance(InitialDelayMilliseconds - 1);
                Commit();
                Attempts().Should().Be(1);
                clock.Advance(1);
                Commit();
                Commit();
                Attempts().Should().Be(2);

                // The delay doubled: another initial delay is not enough any more.
                clock.Advance(InitialDelayMilliseconds);
                Commit();
                Attempts().Should().Be(2);
                clock.Advance(InitialDelayMilliseconds);
                Commit();
                Attempts().Should().Be(3);

                // However long the reader lives, the delay never exceeds its cap.
                for (var i = 0; i < 10; i++)
                {
                    clock.Advance(MaxDelayMilliseconds);
                    Commit();
                }
                Attempts().Should().Be(13);
                await reader.CloseAsync();
            }

            // Still inside the back-off window: a commit that finds no open transaction checkpoints anyway.
            Commit();
            new FileInfo(LogFile(file)).Length.Should().Be(0);
            Attempts().Should().Be(13);

            // Success reset the back-off, so the next long reader is met with a waiting attempt at once.
            using (var reader = HeldReader.Open(engine))
            {
                Commit();
                Attempts().Should().Be(14);
                await reader.CloseAsync();
            }
        }

        [Fact]
        public async Task Closing_inside_the_back_off_window_still_checkpoints()
        {
            using var file = new TempFile();
            using (var engine = new LiteEngine(file.Filename))
            {
                engine.Pragma(Pragmas.CHECKPOINT, 1);
                var backoff = GetBackoff(engine);
                backoff.Timestamp = new FakeClock().Read;
                var before = backoff.WaitingAttempts;
                using (var reader = HeldReader.Open(engine))
                {
                    engine.Insert("rows", new[] { new BsonDocument { ["_id"] = 1 } }, BsonAutoId.Int32);
                    engine.Insert("rows", new[] { new BsonDocument { ["_id"] = 2 } }, BsonAutoId.Int32);
                    await reader.CloseAsync();
                }
                backoff.WaitingAttempts.Should().Be(before + 1);
                new FileInfo(LogFile(file)).Length.Should().BeGreaterThan(0);
            }
            File.Exists(LogFile(file)).Should().BeFalse("closing checkpoints without consulting the back-off");
        }

        private static string LogFile(TempFile file) => Path.ChangeExtension(file.Filename, null) + "-log.db";

        private static CheckpointBackoff GetBackoff(LiteEngine engine)
        {
            var walIndex = typeof(LiteEngine).GetField("_walIndex", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(engine);
            return (CheckpointBackoff)typeof(WalIndexService).GetField("_backoff", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(walIndex);
        }

        private sealed class FakeClock
        {
            private long _now = Stopwatch.Frequency;

            public long Read() => Interlocked.Read(ref _now);

            public void Advance(int milliseconds) => Interlocked.Add(ref _now, milliseconds * Stopwatch.Frequency / 1000);
        }

        private sealed class HeldReader : IDisposable
        {
            private readonly ManualResetEventSlim _release = new ManualResetEventSlim();
            private Task _task;

            public static HeldReader Open(LiteEngine engine)
            {
                var reader = new HeldReader();
                using var ready = new ManualResetEventSlim();
                reader._task = Task.Factory.StartNew(() =>
                {
                    engine.BeginTrans().Should().BeTrue();
                    try
                    {
                        ready.Set();
                        reader._release.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue();
                    }
                    finally { engine.Rollback(); }
                }, TaskCreationOptions.LongRunning);
                ready.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                return reader;
            }

            public Task CloseAsync()
            {
                _release.Set();
                return _task;
            }

            public void Dispose()
            {
                _release.Set();
                _task.Wait(TimeSpan.FromSeconds(10));
                _release.Dispose();
            }
        }
    }
}
