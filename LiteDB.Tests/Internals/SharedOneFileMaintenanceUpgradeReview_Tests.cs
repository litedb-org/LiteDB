#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedOneFileMaintenanceUpgradeReview_Tests
    {
        [OneFileTheory]
        [InlineData("v4.db", null)]
        [InlineData("Issue_2494_EncryptedV4.db", "pass123")]
        public void Explicit_upgrade_must_still_run_before_read_only_mapped_open(string resource, string password)
        {
            using var files = new SharedOneFilePublicationFixture();
            File.Copy("../../../Resources/" + resource, files.Filename, overwrite: true);
            var control = Path.Combine(Path.GetDirectoryName(files.Filename), "control.db");
            File.Copy(files.Filename, control);
            string[] names;
            BsonDocument[][] expected;
            // Existing public contract: Upgrade=true explicitly authorizes the
            // v7 replacement before applying the requested read-only connection.
            using (var direct = new LiteDatabase(new ConnectionString
                { Filename = control, Password = password, Upgrade = true, ReadOnly = true }))
            {
                names = direct.GetCollectionNames().OrderBy(name => name).ToArray();
                names.Should().NotBeEmpty();
                expected = names.Select(name => direct.GetCollection(name).FindAll().ToArray()).ToArray();
            }

            var settings = files.Settings;
            settings.Password = password;
            settings.Upgrade = true;
            settings.ReadOnly = true;
            for (var repeat = 0; repeat < 2; repeat++)
            {
                using var engine = new SharedEngine(settings);
                using var db = new LiteDatabase(engine, disposeOnClose: false);
                db.GetCollectionNames().OrderBy(name => name).Should().Equal(names);
                for (var collection = 0; collection < names.Length; collection++)
                    db.GetCollection(names[collection]).FindAll().Should().BeEquivalentTo(expected[collection]);
                Action write = () => db.GetCollection("review-write").Insert(new BsonDocument { ["_id"] = 1 });
                write.Should().Throw<Exception>("the connection must remain read-only after its explicit upgrade");
            }
        }
    }
}
#endif
