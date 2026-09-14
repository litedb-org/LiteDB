using System.IO;
using System.Linq;

using FluentAssertions;

using LiteDB.Engine;

using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1325_Tests
    {
        [Theory]
        [InlineData(null, false)]
        [InlineData("", true)]
        [InlineData("issue-1325-password", true)]
        public void Engine_settings_password_controls_new_file_encryption_and_reopen(
            string password, bool encrypted)
        {
            using var file = new TempFile();
            var settings = new EngineSettings { Filename = file.Filename, Password = password };
            using (var database = new LiteDatabase(new LiteEngine(settings)))
            {
                var rows = database.GetCollection("rows");
                rows.Insert(new BsonDocument { ["_id"] = 7, ["marker"] = "first-generation" });
                rows.Insert(new BsonDocument { ["_id"] = 19, ["marker"] = "untouched" });
                rows.Update(new BsonDocument { ["_id"] = 7, ["marker"] = "second-generation" })
                    .Should().BeTrue();
                database.Checkpoint();
            }

            // Check persisted encryption independently of the constructor and mapper.
            using (var raw = File.OpenRead(file.Filename))
            {
                raw.ReadByte().Should().Be(encrypted ? 1 : 0);
            }

            // Reopen through the normal connection API rather than the EngineSettings
            // wrapper used by the report. Only null disables encryption; an empty
            // string is still an explicitly supplied password on this API path.
            using var reopened = new LiteDatabase(new ConnectionString
            {
                Filename = file.Filename,
                Password = encrypted ? password : null
            });
            var persisted = reopened.GetCollection("rows");
            persisted.FindAll().Select(row => row["_id"].AsInt32).Should().Equal(7, 19);
            persisted.FindById(7)["marker"].AsString.Should().Be("second-generation");
            persisted.FindById(19)["marker"].AsString.Should().Be("untouched");
            persisted.Count().Should().Be(2);
        }
    }
}
