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
        public void Disposed_engines_reject_metadata_reads_and_mutations()
        {
            var db = new LiteDatabase(":memory:");
            db.Dispose();
            foreach (var action in new Action[]
            {
                () => db.DropCollection("rows"),
                () => db.RenameCollection("rows", "renamed"),
                () => db.Pragma("TIMEOUT"),
                () => db.Pragma("TIMEOUT", 60)
            })
            {
                action.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.ENGINE_DISPOSED);
            }
        }

        [Fact]
        public void Healthy_explicit_transactions_keep_metadata_mutation_restrictions()
        {
            using var db = new LiteDatabase(":memory:");
            db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            db.BeginTrans();
            foreach (var action in new Action[]
            {
                () => db.DropCollection("rows"),
                () => db.RenameCollection("rows", "renamed"),
                () => db.Pragma("TIMEOUT", 30)
            })
            {
                var failure = action.Should().Throw<LiteException>().Which;
                failure.ErrorCode.Should().Be(LiteException.INVALID_TRANSACTION_STATE);
            }
            db.Pragma("TIMEOUT").AsInt32.Should().Be(60);
            db.Rollback();
            db.GetCollection("rows").Count().Should().Be(1);
        }

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
                    () => broken.Pragma("TIMEOUT"),
                    () => broken.Pragma("TIMEOUT", 60),
                    () => broken.Pragma("TIMEOUT", 30),
                    () => broken.GetCollection("b").Insert(new BsonDocument { ["_id"] = 2 })
                })
                {
                    var error = Record.Exception(action);
                    error.Should().BeSameAs(original);
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
