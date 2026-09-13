using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2841_Tests
    {
        [Fact]
        public void Fatal_page_error_remains_the_error_for_subsequent_metadata_operations()
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename))
            {
                db.GetCollection("a").InsertBulk(Enumerable.Range(1, 20).Select(i =>
                    new BsonDocument { ["_id"] = i, ["value"] = new string('x', 2000) }));
                db.GetCollection("b").Insert(new BsonDocument { ["_id"] = 1 });
            }
            var bytes = File.ReadAllBytes(file.Filename);
            var page = Enumerable.Range(1, bytes.Length / 8192 - 1).First(p => bytes[p * 8192 + 4] == 4);
            bytes[page * 8192 + 4] = 3; // Controlled on-disk corruption: Data -> Index page.
            File.WriteAllBytes(file.Filename, bytes);
            using var broken = new LiteDatabase(file.Filename);
            Action trigger = () => broken.DropCollection("a");
            var original = trigger.Should().Throw<LiteException>().Which;
            original.ErrorCode.Should().Be(LiteException.INVALID_DATAFILE_STATE);
            using (new AssertionScope())
            {
                foreach (var action in new Action[]
                {
                    () => broken.DropCollection("b"),
                    () => broken.RenameCollection("b", "c"),
                    () => broken.Pragma("TIMEOUT", 30),
                    () => broken.GetCollection("b").Insert(new BsonDocument { ["_id"] = 2 })
                })
                {
                    var error = Record.Exception(action);
                    error.Should().BeOfType<LiteException>();
                    if (error is LiteException lite)
                    {
                        lite.ErrorCode.Should().Be(original.ErrorCode);
                        lite.Message.Should().Be(original.Message);
                    }
                }
            }
        }
    }
}
