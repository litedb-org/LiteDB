using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>Regression coverage for the Phase 3 same-thread write rule.</summary>
    [Collection("DirectProcessHost")]
    public class SameThreadTransaction_Tests
    {
        [Fact]
        public void Direct_peer_write_fails_immediately_with_the_same_thread_error()
        {
            using var file = new TempFile();
            using var owner = new LiteDatabase(file.Filename);
            using var peer = new LiteDatabase(file.Filename);
            owner.BeginTrans().Should().BeTrue();
            owner.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });

            var clock = Stopwatch.StartNew();
            var failure = Record.Exception(() => peer.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 2 }));

            clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
            failure.Should().BeOfType<LiteException>().Which.ErrorCode.Should().Be(LiteException.SAME_THREAD_TRANSACTION);
            owner.Rollback().Should().BeTrue();
        }

        [Fact]
        public void Legacy_shared_peer_write_fails_immediately_with_the_same_thread_error()
        {
            using var file = new TempFile();
            var settings = new ConnectionString { Filename = file.Filename, Connection = ConnectionType.Shared };
            using var owner = new LiteDatabase(settings);
            using var peer = new LiteDatabase(settings);
            owner.BeginTrans().Should().BeTrue();

            var clock = Stopwatch.StartNew();
            var failure = Record.Exception(() => peer.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 }));

            clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
            failure.Should().BeOfType<LiteException>().Which.ErrorCode.Should().Be(LiteException.SAME_THREAD_TRANSACTION);
            owner.Rollback().Should().BeTrue();
        }

        [Fact]
        public void Direct_foreign_thread_writer_waits_for_commit()
        {
            using var file = new TempFile();
            using var owner = new LiteDatabase(file.Filename);
            using var peer = new LiteDatabase(file.Filename);
            owner.BeginTrans().Should().BeTrue();
            owner.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });

            var write = Task.Run(() => peer.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 2 }));
            Thread.Sleep(100);
            write.IsCompleted.Should().BeFalse();
            owner.Commit().Should().BeTrue();
            write.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            peer.GetCollection("docs").Count().Should().Be(2);
        }

        [Fact]
        public void Foreign_thread_writer_still_waits_and_succeeds_after_commit()
        {
            using var file = new TempFile();
            var settings = new ConnectionString
            {
                Filename = file.Filename,
                Connection = ConnectionType.Shared,
            };
            using var owner = new LiteDatabase(settings);
            using var peer = new LiteDatabase(settings);
            owner.BeginTrans().Should().BeTrue();
            owner.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });

            var write = Task.Run(() => peer.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 2 }));
            Thread.Sleep(100);
            write.IsCompleted.Should().BeFalse();
            owner.Commit().Should().BeTrue();
            write.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            peer.GetCollection("docs").Count().Should().Be(2);
        }

#if NET8_0_OR_GREATER
        [OneFileFact]
        public void Mapped_shared_peer_write_fails_immediately_with_the_same_thread_error()
        {
            var directory = Path.Combine(SharedMappedDirectory.Root, "same-thread-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var filename = Path.Combine(directory, "mapped.db");
            var settings = new EngineSettings { Filename = filename, SharedOneFile = true };
            try
            {
                using var first = new SharedEngine(settings);
                using var second = new SharedEngine(settings);
                using var owner = new LiteDatabase(first, disposeOnClose: false);
                using var peer = new LiteDatabase(second, disposeOnClose: false);
                owner.BeginTrans().Should().BeTrue();

                var clock = Stopwatch.StartNew();
                var failure = Record.Exception(() => peer.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 }));

                clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
                failure.Should().BeOfType<LiteException>().Which.ErrorCode.Should().Be(LiteException.SAME_THREAD_TRANSACTION);
                owner.Rollback().Should().BeTrue();
            }
            finally
            {
                try { Directory.Delete(directory, true); }
                catch (DirectoryNotFoundException) { }
            }
        }

        [OneFileFact]
        public void Mapped_shared_foreign_thread_writer_waits_for_commit()
        {
            var directory = Path.Combine(SharedMappedDirectory.Root, "same-thread-foreign-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var filename = Path.Combine(directory, "mapped.db");
                using var first = new SharedEngine(new EngineSettings { Filename = filename, SharedOneFile = true });
                using var second = new SharedEngine(new EngineSettings
                {
                    Filename = filename,
                    SharedOneFile = true,
                });
                using var owner = new LiteDatabase(first, disposeOnClose: false);
                using var peer = new LiteDatabase(second, disposeOnClose: false);
                owner.BeginTrans().Should().BeTrue();

                var write = Task.Run(() => peer.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 }));
                Thread.Sleep(100);
                write.IsCompleted.Should().BeFalse();
                owner.Commit().Should().BeTrue();
                write.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            }
            finally
            {
                try { Directory.Delete(directory, true); }
                catch (DirectoryNotFoundException) { }
            }
        }

#endif
    }
}
