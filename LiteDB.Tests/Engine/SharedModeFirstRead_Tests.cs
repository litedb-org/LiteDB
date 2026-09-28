#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Internals;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class SharedModeFirstRead_Tests
    {
        [MappedTheory]
        [InlineData("absent", null)]
        [InlineData("empty", null)]
        [InlineData("mismatched", null)]
        [InlineData("absent", "secret")]
        [InlineData("empty", "secret")]
        [InlineData("mismatched", "secret")]
        [InlineData("absent-disposed", null)]
        [InlineData("absent-disposed", "secret")]
        public async Task First_streaming_read_on_writable_connection_keeps_direct_writers_out(string identity, string password)
        {
            using var file = new MappedTestFile();
            using (var seed = new LiteDatabase(new ConnectionString { Filename = file, Password = password }))
            {
                seed.GetCollection("rows").InsertBulk(Enumerable.Range(0, 3000)
                    .Select(id => new BsonDocument { ["_id"] = id, ["value"] = 0 }));
                seed.GetCollection("rows").EnsureIndex("value");
                seed.GetCollection("untouched").Insert(new BsonDocument { ["_id"] = 1, ["value"] = 42 });
            }
            if (identity.StartsWith("absent", StringComparison.Ordinal)) File.Delete(file.Filename + "-shared-mode");
            if (identity == "mismatched")
                using (SharedModeGuard.Open(file, true, SharedMutexNameStrategy.Sha1Hash)) { }
            using (var engine = new SharedEngine(new EngineSettings { Filename = file, Password = password }))
            using (var db = new LiteDatabase(engine))
            using (var reader = db.GetCollection("rows").FindAll().GetEnumerator())
            {
                reader.MoveNext().Should().BeTrue();
                var data = TempFile.ReadAllBytesShared(file);
                if (identity == "absent-disposed") engine.Dispose();
                await MvccProcess.Run("mode-direct-rejected", file, password);
                await MvccProcess.Run("mode-mutex-rejected", file, password);
                Action write = () =>
                {
                    using var direct = new LiteDatabase(new ConnectionString { Filename = file, Password = password });
                    direct.GetCollection("rows").UpdateMany("{ value: 1 }", "true");
                    direct.Checkpoint();
                };
                write.Should().Throw<IOException>();
                var count = 1;
                reader.Current["value"].AsInt32.Should().Be(0);
                while (reader.MoveNext()) { reader.Current["value"].AsInt32.Should().Be(0); count++; }
                count.Should().Be(3000);
                TempFile.ReadAllBytesShared(file).Should().Equal(data);
            }
            using var cold = new LiteDatabase(new ConnectionString { Filename = file, Password = password });
            cold.GetCollection("rows").Find("value = 0").Count().Should().Be(3000);
            cold.GetCollection("untouched").FindById(1)["value"].AsInt32.Should().Be(42);
        }

        [MappedTheory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void Rejected_mutex_identity_does_not_revoke_peer_or_make_retry_sticky(bool readOnly, bool disabled)
        {
            using var file = new MappedTestFile();
            using var peer = new SharedEngine(new EngineSettings { Filename = file })
                { CoordinatedIdleLimit = TimeSpan.FromMinutes(1) };
            using var db = new LiteDatabase(peer);
            db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = 0 });
            for (var i = 0; i < 4; i++) db.GetCollection("rows").FindById(1);
            peer.GetDiagnostics().ReadPath.Should().Be(SharedReadPath.Mapped);
            AppContext.TryGetSwitch(SharedCoordinationPolicy.DisableMappedSwitch, out var before);
            SharedEngine rejected;
            AppContext.SetSwitch(SharedCoordinationPolicy.DisableMappedSwitch, disabled);
            try
            {
                rejected = new SharedEngine(new EngineSettings
                { Filename = file, ReadOnly = readOnly, SharedMutexNameStrategy = SharedMutexNameStrategy.Sha1Hash });
            }
            finally { AppContext.SetSwitch(SharedCoordinationPolicy.DisableMappedSwitch, before); }
            using (rejected)
            using (var other = new LiteDatabase(rejected))
            {
                var hits = peer.GetDiagnostics().CoordinatedReadHits;
                Action attempt = () =>
                {
                    if (readOnly) other.GetCollection("rows").FindById(1);
                    else other.GetCollection("rows").DeleteAll();
                };
                attempt.Should().Throw<IOException>();
                File.Exists(SharedCoordinationFallback.DisabledPath(file)).Should().BeFalse();
                peer.GetDiagnostics().ReadPath.Should().Be(SharedReadPath.Mapped);
                db.GetCollection("rows").FindById(1)["value"].AsInt32.Should().Be(0);
                peer.GetDiagnostics().CoordinatedReadHits.Should().BeGreaterThan(hits);
                peer.Dispose();
                for (var i = 0; i < 4; i++) other.GetCollection("rows").FindById(1)["value"].AsInt32.Should().Be(0);
                if (!readOnly && !disabled) rejected.GetDiagnostics().ReadPath.Should().Be(SharedReadPath.Mapped);
            }
            using var cold = new LiteDatabase(file);
            cold.GetCollection("rows").Count().Should().Be(1);
        }
    }
}
#endif
