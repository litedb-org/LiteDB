using System.IO;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2765_Tests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("incorrect")]
        public void Missing_and_wrong_passwords_report_structured_errors_without_altering_data(string password)
        {
            using var file = new TempFile();
            var correct = new ConnectionString { Filename = file.Filename, Password = "regression-password" };
            using (var db = new LiteDatabase(correct))
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["payload"] = "secret" });
            var original = File.ReadAllBytes(file.Filename);
            var failure = Record.Exception(() =>
            {
                using var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, Password = password });
                db.GetCollection("rows").Count();
            });
            using (new AssertionScope())
            {
                failure.Should().BeOfType<LiteException>();
                if (failure is LiteException lite) lite.ErrorCode.Should().Be(LiteException.INVALID_PASSWORD);
                File.ReadAllBytes(file.Filename).Should().Equal(original);
                using var db = new LiteDatabase(correct);
                db.GetCollection("rows").FindById(1)["payload"].AsString.Should().Be("secret");
            }
        }
    }
}
