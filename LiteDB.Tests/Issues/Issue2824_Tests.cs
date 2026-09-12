using System;
using System.IO;
using FluentAssertions;
using FluentAssertions.Execution;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2824_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Encrypted_rebuild_keeps_or_changes_password_without_losing_instance_or_data(bool changePassword)
        {
            var directory = Path.Combine(Path.GetTempPath(), "litedb-2824-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "data.db");
            var password = changePassword ? "new-password" : "old-password";
            try
            {
                Exception failure;
                using (var db = new LiteDatabase(new ConnectionString { Filename = file, Password = "old-password" }))
                {
                    db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "encrypted payload" });
                    failure = Record.Exception(() =>
                    {
                        if (changePassword) db.Rebuild(new RebuildOptions { Password = password });
                        else db.Rebuild();
                    });
                    if (failure == null)
                    {
                        db.GetCollection("rows").FindById(1)["value"].AsString.Should().Be("encrypted payload");
                        db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2, ["value"] = "after rebuild" });
                    }
                }
                using (new AssertionScope())
                {
                    failure.Should().BeNull();
                    File.ReadAllBytes(file)[0].Should().Be(1, "a parameterless rebuild must not remove encryption");
                    Action plaintext = () => { using var wrong = new LiteDatabase(file); wrong.GetCollection("rows").Count(); };
                    plaintext.Should().Throw<LiteException>();
                    if (failure == null)
                    {
                        using (var reopened = new LiteDatabase(new ConnectionString { Filename = file, Password = password }))
                        {
                            reopened.GetCollection("rows").FindById(1)["value"].AsString.Should().Be("encrypted payload");
                            reopened.GetCollection("rows").FindById(2)["value"].AsString.Should().Be("after rebuild");
                        }
                        if (changePassword)
                        {
                            Action old = () => { using var wrong = new LiteDatabase(new ConnectionString { Filename = file, Password = "old-password" }); };
                            old.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.INVALID_PASSWORD);
                        }
                    }
                }
            }
            finally { Directory.Delete(directory, true); }
        }
    }
}
