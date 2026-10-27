#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedOneFileReadOnlyUpgradeControls_Tests
    {
        [OneFileTheory]
        [InlineData("v4.db", null)]
        [InlineData("Issue_2494_EncryptedV4.db", "pass123")]
        public void Explicit_upgrade_also_precedes_internal_read_only_storage(string resource, string password)
        {
            using var files = new SharedOneFilePublicationFixture();
            File.Copy("../../../Resources/" + resource, files.Filename, overwrite: true);
            var control = Path.Combine(Path.GetDirectoryName(files.Filename), "control.db");
            File.Copy(files.Filename, control);
            var directSettings = new EngineSettings { Filename = control, Password = password, Upgrade = true, ReadOnlyStorage = true };
            string[] names;
            BsonDocument[][] expected;
            using (var direct = new LiteDatabase(new LiteEngine(directSettings)))
            {
                names = direct.GetCollectionNames().OrderBy(n => n).ToArray();
                expected = names.Select(n => direct.GetCollection(n).FindAll().ToArray()).ToArray();
            }
            var settings = files.Settings;
            settings.Password = password;
            settings.Upgrade = true;
            settings.ReadOnlyStorage = true;
            for (var repeat = 0; repeat < 2; repeat++)
            {
                using var db = new LiteDatabase(new SharedEngine(settings));
                db.GetCollectionNames().OrderBy(n => n).Should().Equal(names);
                for (var i = 0; i < names.Length; i++) db.GetCollection(names[i]).FindAll().Should().BeEquivalentTo(expected[i]);
                Action write = () => db.GetCollection("forbidden").Insert(new BsonDocument { ["_id"] = 1 });
                write.Should().Throw<Exception>();
            }
        }

        [OneFileTheory]
        [InlineData(null)]
        [InlineData("secret")]
        public void No_op_explicit_upgrade_keeps_modern_storage_read_only(string password)
        {
            using var files = new SharedOneFilePublicationFixture(password);
            SharedOneFileRebuildPowerLoss_Tests.SeedAcknowledgedWal(files);
            var settings = files.Settings;
            settings.ReadOnly = true;
            settings.Upgrade = true;
            var data = File.ReadAllBytes(files.Filename);
            var wal = File.ReadAllBytes(files.Log);
            for (var repeat = 0; repeat < 2; repeat++)
            {
                using (var db = new LiteDatabase(new SharedEngine(settings))) Verify(db, 11);
                File.ReadAllBytes(files.Filename).Should().Equal(data);
                File.ReadAllBytes(files.Log).Should().Equal(wal);
                File.Exists(RebuildRecovery.GetMarkerFilename(files.Filename)).Should().BeFalse();
            }
        }
    }
}
#endif
