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
                string[] names;
                using (var db = new LiteDatabase(new ConnectionString
                {
                    Filename = file.Filename, Password = password, Upgrade = true, ReadOnly = true
                }))
                {
                    names = db.GetCollectionNames().ToArray();
                    names.Should().NotBeEmpty();
                    foreach (var name in names) db.GetCollection(name).FindAll().ToArray();
                    if (password == null) db.GetCollection("col1").Count().Should().Be(3);
                    using (var parallel = new LiteDatabase(new ConnectionString
                    {
                        Filename = file.Filename, Password = password, ReadOnly = true
                    }))
                        parallel.GetCollectionNames().Should().BeEquivalentTo(names);
                    Action write = () => db.GetCollection("new_collection").Insert(new BsonDocument { ["_id"] = 1 });
                    // The missing read-only WAL now uses the same contextual
                    // FILE_NOT_FOUND diagnostic as a missing read-only database.
                    var error = write.Should().Throw<LiteException>().Which;
                    error.ErrorCode.Should().Be(LiteException.FILE_NOT_FOUND);
                    error.InnerException.Should().BeOfType<FileNotFoundException>();
                    error.Message.Should().Contain("read-only");
                }
                File.ReadAllBytes(backup).Should().Equal(original);
                using var reopened = new LiteDatabase(new ConnectionString { Filename = file.Filename, Password = password, ReadOnly = true });
                // A failed WAL write stops the old engine; verify durable state
                // through a fresh reader rather than reusing that faulted engine.
                reopened.GetCollectionNames().Should().BeEquivalentTo(names);
            }
            finally
            {
                File.Delete(backup);
            }
        }
    }
}
