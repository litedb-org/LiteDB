using System;
using System.IO;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2211_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Valid_filename_with_equals_sign_opens_the_requested_file(bool viaConnectionString)
        {
            var directory = Path.Combine(Path.GetTempPath(), "litedb-equals-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "name=value.db");
            try
            {
                using (var db = viaConnectionString ? new LiteDatabase(new ConnectionString(path)) : new LiteDatabase(path))
                    db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 7, ["value"] = "correct path" });
                File.Exists(path).Should().BeTrue();
                using (var db = new LiteDatabase(new ConnectionString { Filename = path }))
                    db.GetCollection("rows").FindById(7)["value"].AsString.Should().Be("correct path");
            }
            finally { Directory.Delete(directory, true); }
        }
    }
}
