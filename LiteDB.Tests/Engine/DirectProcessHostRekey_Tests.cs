using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Direct;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    [Collection("DirectProcessHost")]
    public class DirectProcessHostRekey_Tests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedb-host-rekey-" + Guid.NewGuid().ToString("N"));
        private string Filename => Path.Combine(_directory, "test.db");

        public DirectProcessHostRekey_Tests() => Directory.CreateDirectory(_directory);

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Replacement_identity_cannot_publish_a_second_core_before_maintenance_returns(bool foreignThread)
        {
            // The verifier's distinct-host/stale-read/data-loss scenario, with the
            // schedule forced by retaining the real host maintenance lease across
            // the completed core rebuild. This uses APIs present at 45b85dc2e.
            var host = DirectProcessHost.Attach(new EngineSettings { Filename = Filename });
            try
            {
                using var owner = new LiteDatabase(host.Engine, disposeOnClose: false);
                Seed(owner);
                host.Maintain(() =>
                {
                    host.Engine.Rebuild();
                    Action attach = () => { using var peer = new LiteDatabase(Filename); };
                    if (foreignThread)
                        Task.Run(() => attach.Should().Throw<InvalidOperationException>().WithMessage("*maintenance*")).GetAwaiter().GetResult();
                    else
                        attach.Should().Throw<InvalidOperationException>().WithMessage("*maintenance*");
                    return 0;
                });
                WriteThroughBoth(owner, Filename);
            }
            finally { host.Release(null); }
            VerifyCold();
        }

        [Theory]
        [InlineData(false, null, false)]
        [InlineData(true, null, false)]
        [InlineData(false, "secret", false)]
        [InlineData(true, "secret", false)]
        [InlineData(false, null, true)]
        [InlineData(true, null, true)]
        [InlineData(false, "secret", true)]
        [InlineData(true, "secret", true)]
        public void Facade_rebuild_keeps_admission_at_every_rekey_stage_and_until_maintenance_release(
            bool alias, string password, bool failAfterRebuild)
        {
            using (var owner = Open(Filename, password))
            {
                Seed(owner);
                var peerPath = alias ? CreateAlias() : Filename;
                var canonical = DatabaseFileIdentity.CanonicalPath(Filename);
                var hits = new List<string>();
                var completed = 0;
                // These callbacks run reentrantly on the rebuilding thread under
                // NativeAdmission.Gate. Other-thread admission is serialized by
                // that gate across the entire native swap, and tested below once
                // it can run, while the facade still owns maintenance.
                NativeAdmission.ReplacementStage = (filename, stage) =>
                {
                    if (filename != canonical) return;
                    hits.Add(stage);
                    Action attach = () => { using var peer = Open(peerPath, password); };
                    if (stage == "replacement-published")
                        attach.Should().Throw<InvalidOperationException>().WithMessage("*maintenance*");
                    else
                        attach.Should().Throw<IOException>().WithMessage("*admission refused*");
                };
                DirectProcessHost.BeforeMaintenanceRelease = filename =>
                {
                    if (filename != canonical) return;
                    ++completed;
                    var opening = Task.Factory.StartNew(() =>
                    {
                        // The maintenance gate is per host, so a different file
                        // must remain usable while this facade holds its lease.
                        using (var unrelated = new LiteDatabase(Path.Combine(_directory, "other.db")))
                            unrelated.GetCollection("docs").Upsert(Row(42));
                        Action attach = () => { using var peer = Open(peerPath, password); };
                        attach.Should().Throw<InvalidOperationException>().WithMessage("*maintenance*");
                    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                    opening.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("attach must refuse without waiting for its own maintenance lease");
                    opening.GetAwaiter().GetResult();
                    if (failAfterRebuild) throw new IOException("maintenance completion probe");
                };
                try
                {
                    for (var rebuild = 0; rebuild < 2; ++rebuild)
                    {
                        Action action = () => owner.Rebuild();
                        if (failAfterRebuild) action.Should().Throw<IOException>().WithMessage("maintenance completion probe");
                        else action();
                    }
                }
                finally
                {
                    NativeAdmission.ReplacementStage = null;
                    DirectProcessHost.BeforeMaintenanceRelease = null;
                }
                hits.Should().Equal(Enumerable.Range(0, 2).SelectMany(_ => new[]
                    { "before-rekey", "old-identity-removed", "replacement-assigned", "replacement-published" }));
                completed.Should().Be(2);
                WriteThroughBoth(owner, peerPath, password);
            }
            VerifyCold(password);
            using var other = new LiteDatabase(Path.Combine(_directory, "other.db"));
            Assert.Equal<BsonValue>(Row(42), other.GetCollection("docs").FindById(42));
        }

        private string CreateAlias()
        {
#if !NETFRAMEWORK
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var alias = Path.Combine(_directory, "alias.db");
                File.CreateSymbolicLink(alias, Filename);
                return alias;
            }
#endif
            // No symlink privilege is required on Windows/Framework.
            return Path.Combine(_directory, ".", "test.db");
        }

        private static LiteDatabase Open(string filename, string password) =>
            new LiteDatabase(new ConnectionString { Filename = filename, Password = password });

        private static void Seed(LiteDatabase database)
        {
            database.CheckpointSize = 0;
            database.GetCollection("docs").EnsureIndex("value");
            database.GetCollection("docs").Insert(Row(1));
            database.GetCollection("unrelated").Insert(new BsonDocument { ["_id"] = 99, ["sentinel"] = "keep" });
        }

        private static BsonDocument Row(int id) => new BsonDocument
        {
            ["_id"] = id, ["value"] = "row-" + id, ["payload"] = new string((char)('a' + id), 9000)
        };

        private static void WriteThroughBoth(LiteDatabase owner, string filename, string password = null)
        {
            using var peer = Open(filename, password);
            owner.GetCollection("docs").Insert(Row(7001));
            Assert.Equal<BsonValue>(Row(7001), peer.GetCollection("docs").FindById(7001));
            peer.GetCollection("docs").Insert(Row(7002));
            Assert.Equal<BsonValue>(Row(7002), owner.GetCollection("docs").FindById(7002));
        }

        private void VerifyCold(string password = null)
        {
            for (var reopen = 0; reopen < 2; ++reopen)
            {
                using var cold = Open(Filename, password);
                var docs = cold.GetCollection("docs");
                docs.FindAll().Select(x => x["_id"].AsInt32).Should().BeEquivalentTo(new[] { 1, 7001, 7002 });
                foreach (var id in new[] { 1, 7001, 7002 })
                {
                    Assert.Equal<BsonValue>(Row(id), docs.FindById(id));
                    Assert.Equal<BsonValue>(Row(id), docs.FindOne(Query.EQ("value", "row-" + id)));
                }
                cold.GetCollection("unrelated").FindById(99)["sentinel"].AsString.Should().Be("keep");
            }
        }

        public void Dispose() => WindowsFileRelease.DeleteDirectory(_directory);
    }
}
