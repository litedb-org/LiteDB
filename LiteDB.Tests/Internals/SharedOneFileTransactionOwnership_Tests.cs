#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Threading;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;

namespace LiteDB.Internals
{
    public sealed class SharedOneFileTransactionOwnership_Tests : IDisposable
    {
        private readonly string _directory = Path.Combine(SharedMappedDirectory.Root, "litedb-shm-owner-" + Guid.NewGuid().ToString("N"));
        private string Filename => Path.Combine(_directory, "test.db");

        public SharedOneFileTransactionOwnership_Tests() => Directory.CreateDirectory(_directory);

        [OneFileFact]
        public void Compatible_facades_share_one_owner_and_refuse_same_thread_peer_waits()
        {
            var settings = new EngineSettings { Filename = Filename, SharedOneFile = true };
            using var first = new SharedEngine(settings);
            using var second = new SharedEngine(settings);
            using var owner = new LiteDatabase(first, disposeOnClose: false);
            using var peer = new LiteDatabase(second, disposeOnClose: false);

            owner.BeginTrans().Should().BeTrue();
            owner.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
            Action peerBegin = () => peer.BeginTrans();
            peerBegin.Should().Throw<InvalidOperationException>().WithMessage("*another connection*");
            owner.Commit().Should().BeTrue();
            peer.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 2 });
            peer.GetCollection("docs").Count().Should().Be(2);
        }

        [OneFileFact]
        public void Failed_mapped_write_releases_owner_before_peer_and_retry()
        {
            var settings = new EngineSettings { Filename = Filename, SharedOneFile = true };
            using var first = new SharedEngine(settings);
            using var second = new SharedEngine(settings);
            using var owner = new LiteDatabase(first, disposeOnClose: false);
            using var peer = new LiteDatabase(second, disposeOnClose: false);

            owner.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
            owner.BeginTrans().Should().BeTrue();
            Action duplicate = () => owner.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
            duplicate.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(110);

            owner.BeginTrans().Should().BeTrue();
            owner.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 2 });
            owner.Commit().Should().BeTrue();

            peer.BeginTrans().Should().BeTrue();
            peer.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 3 });
            peer.Commit().Should().BeTrue();
            owner.GetCollection("docs").Count().Should().Be(3);
        }

        [OneFileFact]
        public void A_dead_owner_releases_the_local_gate_and_native_mutex()
        {
            var settings = new EngineSettings { Filename = Filename, SharedOneFile = true };
            SharedEngine abandoned = null;
            LiteDatabase abandonedDatabase = null;
            using var started = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                abandoned = new SharedEngine(settings);
                abandonedDatabase = new LiteDatabase(abandoned, disposeOnClose: false);
                abandonedDatabase.BeginTrans().Should().BeTrue();
                abandonedDatabase.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
                started.Set();
            });
            thread.Start();
            started.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            thread.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();

            using var peerEngine = new SharedEngine(settings);
            using var peer = new LiteDatabase(peerEngine, disposeOnClose: false);
            var first = Record.Exception(() => peer.BeginTrans());
            if (first != null) first.Should().BeOfType<LiteException>().Which.Message.Should().Contain("owner thread exited");
            if (first != null) peer.BeginTrans().Should().BeTrue();
            peer.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 2 });
            peer.Commit().Should().BeTrue();
            peer.GetCollection("docs").Count().Should().Be(1);
            abandonedDatabase.Dispose();
            abandoned.Dispose();
        }

        [OneFileFact]
        public void A_consistent_same_layout_authority_is_reused_after_last_close()
        {
            var settings = new EngineSettings { Filename = Filename, SharedOneFile = true };
            var reuseBefore = SharedMemoryAuthority.BootstrapReuseCount;
            Guid authority;
            using (var first = new SharedEngine(settings)) authority = first.MappedReaderTable.Authority.Header.Authority;
            using var second = new SharedEngine(settings);
            second.MappedReaderTable.Authority.Header.Authority.Should().Be(authority);
            SharedMemoryAuthority.BootstrapReuseCount.Should().BeGreaterThan(reuseBefore);
        }

        public void Dispose()
        {
            try { Directory.Delete(_directory, true); }
            catch (DirectoryNotFoundException) { }
        }
    }
}
#endif
