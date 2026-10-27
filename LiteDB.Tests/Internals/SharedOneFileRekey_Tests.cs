#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedOneFileRekey_Tests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Legacy_Shared_replacement_retains_native_entry_and_idle_peers_reopen_the_new_core(string password)
        {
            using var file = new TempFile();
            var settings = new EngineSettings { Filename = file.Filename, Password = password };
            using (var engine = new SharedEngine(settings))
            using (var owner = new LiteDatabase(engine, disposeOnClose: false))
            using (var peerEngine = new SharedEngine(settings))
            using (var peer = new LiteDatabase(peerEngine, disposeOnClose: false))
            using (var admission = NativeAdmission.Open(settings, true))
            {
                owner.GetCollection("docs").Insert(Row(0));
                owner.GetCollection("docs").EnsureIndex("value");
                owner.GetCollection("sentinel").Insert(Row(42));
                Verify(peer, 0); // peer has opened the pre-rebuild storage
                var key = admission.RegistryKey;
                var filename = settings.Filename;
                var stages = new List<string>();
                NativeAdmission.ReplacementStage = (path, stage) =>
                {
                    if (path != filename) return;
                    stages.Add(stage);
                    Action attach = () =>
                    {
                        using var retained = NativeAdmission.Open(settings, true);
                        retained.RegistryKey.Should().BeSameAs(key);
                    };
                    if (stage == "replacement-published") attach();
                    else attach.Should().Throw<IOException>().WithMessage("*admission refused*");
                    Action read = () => peer.GetCollection("docs").Count();
                    read.Should().Throw<InvalidOperationException>("a peer callback cannot enter its own outer Shared rebuild");
                };
                try
                {
                    for (var rebuild = 0; rebuild < 2; rebuild++) owner.Rebuild();
                }
                finally { NativeAdmission.ReplacementStage = null; }
                foreach (var stage in new[] { "before-rekey", "old-identity-removed", "replacement-assigned", "replacement-published" })
                    stages.Count(value => value == stage).Should().Be(2);
                owner.GetCollection("docs").Insert(Row(1));
                Verify(peer, 0, 1);
                peer.GetCollection("docs").Insert(Row(2));
                Verify(owner, 0, 1, 2);
            }
            for (var repeat = 0; repeat < 2; repeat++)
            {
                using var cold = new LiteDatabase(new ConnectionString { Filename = file.Filename, Password = password });
                Verify(cold, 0, 1, 2);
                cold.Checkpoint();
            }
        }

        [OneFileTheory]
        [InlineData("rebuild")]
        [InlineData("upgrade")]
        [InlineData("autorebuild")]
        public void Mapped_Shared_refuses_every_file_replacement_entry_before_native_rekey(string operation)
        {
            using var files = new SharedOneFilePublicationFixture();
            using (var engine = new SharedEngine(files.Settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                var table = engine.MappedReaderTable;
                var key = table.Admission.RegistryKey;
                var identity = table.Admission.Identity;
                var data = File.ReadAllBytes(files.Filename);
                var log = File.Exists(files.Log) ? File.ReadAllBytes(files.Log) : Array.Empty<byte>();
                var rekey = 0;
                NativeAdmission.ReplacementStage = (path, stage) => { if (path == files.Filename) rekey++; };
                try
                {
                    Action replace = () =>
                    {
                        if (operation == "rebuild") db.Rebuild();
                        else
                        {
                            var settings = files.Settings;
                            settings.Upgrade = operation == "upgrade";
                            settings.AutoRebuild = operation == "autorebuild";
                            using var requested = new SharedEngine(settings);
                        }
                    };
                    // A live participant rejects incompatible opening settings;
                    // a cold participant below rejects the replacement capability.
                    if (operation == "rebuild") replace.Should().Throw<NotSupportedException>();
                    else replace.Should().Throw<InvalidOperationException>();
                    rekey.Should().Be(0);
                    File.ReadAllBytes(files.Filename).Should().Equal(data);
                    (File.Exists(files.Log) ? File.ReadAllBytes(files.Log) : Array.Empty<byte>()).Should().Equal(log);
                    using var peer = new SharedEngine(files.Settings);
                    peer.MappedReaderTable.Should().BeSameAs(table);
                    table.Admission.RegistryKey.Should().BeSameAs(key);
                    table.Admission.Identity.Should().Be(identity);
                    SharedOneFilePublicationFixture.Verify(db, 0);
                }
                finally { NativeAdmission.ReplacementStage = null; }
            }
            if (operation != "rebuild")
            {
                var settings = files.Settings;
                settings.Upgrade = operation == "upgrade";
                settings.AutoRebuild = operation == "autorebuild";
                Action coldReplace = () => { using var refused = new SharedEngine(settings); };
                coldReplace.Should().Throw<NotSupportedException>();
            }
            for (var repeat = 0; repeat < 2; repeat++)
            {
                using var cold = new LiteDatabase(files.Filename);
                SharedOneFilePublicationFixture.Verify(cold, 0);
                cold.Checkpoint();
            }
        }

        private static BsonDocument Row(int id) => new BsonDocument
        { ["_id"] = id, ["value"] = id, ["payload"] = new string((char)('a' + id), 9000) };

        private static void Verify(LiteDatabase db, params int[] ids)
        {
            db.GetCollection("docs").FindAll().Should().BeEquivalentTo(ids.Select(Row));
            foreach (var id in ids)
            {
                Assert.Equal<BsonValue>(Row(id), db.GetCollection("docs").FindById(id));
                db.GetCollection("docs").Find(Query.EQ("value", id)).Should().BeEquivalentTo(new[] { Row(id) });
            }
            db.GetCollection("sentinel").FindAll().Should().BeEquivalentTo(new[] { Row(42) });
        }
    }
}
#endif
