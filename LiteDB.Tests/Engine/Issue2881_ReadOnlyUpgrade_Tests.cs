using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class Issue2881_ReadOnlyUpgrade_Tests
    {
        [Theory]
        [InlineData("v4.db", null)]
        [InlineData("Issue_2494_EncryptedV4.db", "pass123")]
        public void Explicit_v7_upgrade_precedes_read_only_access(string resource, string password)
        {
            using var file = new TempFile("../../../Resources/" + resource);
            var original = File.ReadAllBytes(file.Filename);
            var backup = Path.ChangeExtension(file.Filename, null) + "-backup.db";
            try
            {
                using (var db = new LiteDatabase(new ConnectionString
                {
                    Filename = file.Filename, Password = password, Upgrade = true, ReadOnly = true
                }))
                {
                    var names = db.GetCollectionNames().ToArray();
                    names.Should().NotBeEmpty();
                    foreach (var name in names) db.GetCollection(name).FindAll().ToArray();
                    if (password == null) db.GetCollection("col1").Count().Should().Be(3);
                    Action write = () => db.GetCollection("new_collection").Insert(new BsonDocument { ["_id"] = 1 });
                    write.Should().Throw<IOException>();
                }
                File.ReadAllBytes(backup).Should().Equal(original);
                using var reopened = new LiteDatabase(new ConnectionString { Filename = file.Filename, Password = password, ReadOnly = true });
                reopened.GetCollectionNames().Should().NotBeEmpty();
            }
            finally
            {
                File.Delete(backup);
            }
        }
    }
}
