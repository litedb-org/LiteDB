using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using LiteDB.Engine;
using FluentAssertions.Execution;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2765_Tests
    {
        [Fact]
        public void Missing_password_on_a_caller_stream_keeps_the_stream_and_ciphertext_intact()
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, Password = "secret" }))
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["payload"] = "preserved" });
            var original = File.ReadAllBytes(file.Filename);
            using var stream = new MemoryStream(original.ToArray());
            Action open = () => { using var db = new LiteDatabase(stream); };
            open.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.INVALID_PASSWORD);
            stream.CanRead.Should().BeTrue();
            stream.ToArray().Should().Equal(original);
            using var reopened = new LiteDatabase(new LiteEngine(new EngineSettings { DataStream = stream, Password = "secret" }));
            reopened.GetCollection("rows").FindById(1)["payload"].AsString.Should().Be("preserved");
        }

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
                if (failure is LiteException lite)
                {
                    if (password != null)
                    {
                        lite.ErrorCode.Should().Be(LiteException.INVALID_PASSWORD);
                    }
                    else
                    {
                        var passwordCodes = typeof(LiteException)
                            .GetFields(BindingFlags.Public | BindingFlags.Static)
                            .Where(field => field.FieldType == typeof(int))
                            .Where(field =>
                                field.Name.IndexOf("PASSWORD", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                field.Name.IndexOf("ENCRYPT", StringComparison.OrdinalIgnoreCase) >= 0)
                            .Select(field => (int)field.GetValue(null))
                            .ToArray();

                        lite.ErrorCode.Should().NotBe(0);
                        passwordCodes.Should().Contain(lite.ErrorCode,
                            "a missing password needs a public password/encryption-specific error classification");
                    }
                }
                File.ReadAllBytes(file.Filename).Should().Equal(original);
                using var db = new LiteDatabase(correct);
                db.GetCollection("rows").FindById(1)["payload"].AsString.Should().Be("secret");
            }
        }
    }
}
