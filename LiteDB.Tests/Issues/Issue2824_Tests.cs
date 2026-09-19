using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2824_Tests
    {
        private const string OldPassword = "old-password-2824";
        private const string NewPassword = "new-password-2824";
        private const string WrongPassword = "wrong-password-2824";

        [Theory]
        [InlineData(ConnectionType.Direct)]
        [InlineData(ConnectionType.Shared)]
        public void Parameterless_rebuild_retains_encryption_and_the_current_password(ConnectionType connection)
        {
            RunWithDatabase(file =>
            {
                CreateDatabase(file, OldPassword);

                using (var db = Open(file, OldPassword, connection))
                {
                    db.Rebuild();

                    AssertLedger(db, "11=before-alpha", "29=before-omega");
                    InsertAfterRebuild(db);
                }

                File.ReadAllBytes(file)[0].Should().Be(1,
                    "a parameterless rebuild of an encrypted file must remain encrypted");

                AssertRejectedOpensDidNotChangeFile(file, () =>
                {
                    AssertRejected(file, null);
                    AssertRejected(file, WrongPassword, LiteException.INVALID_PASSWORD);
                    AssertRejected(file, NewPassword, LiteException.INVALID_PASSWORD);
                });

                using (var reopened = Open(file, OldPassword))
                {
                    AssertCompleteLedger(reopened);
                }
            });
        }

        [Theory]
        [InlineData(ConnectionType.Direct)]
        [InlineData(ConnectionType.Shared)]
        public void Rebuild_with_a_new_password_propagates_it_to_the_active_engine(ConnectionType connection)
        {
            RunWithDatabase(file =>
            {
                CreateDatabase(file, OldPassword);

                using (var db = Open(file, OldPassword, connection))
                {
                    db.Rebuild(new RebuildOptions { Password = NewPassword });

                    AssertLedger(db, "11=before-alpha", "29=before-omega");
                    InsertAfterRebuild(db);
                }

                File.ReadAllBytes(file)[0].Should().Be(1,
                    "changing a password must leave an encrypted data file");

                AssertRejectedOpensDidNotChangeFile(file, () =>
                {
                    AssertRejected(file, null);
                    AssertRejected(file, OldPassword, LiteException.INVALID_PASSWORD);
                });

                using (var reopened = Open(file, NewPassword))
                {
                    AssertCompleteLedger(reopened);
                }
            });
        }

        [Theory]
        [InlineData(ConnectionType.Direct)]
        [InlineData(ConnectionType.Shared)]
        public void Explicit_password_removal_decrypts_without_disposing_the_active_engine(ConnectionType connection)
        {
            RunWithDatabase(file =>
            {
                CreateDatabase(file, OldPassword);

                using (var db = Open(file, OldPassword, connection))
                {
                    db.Rebuild(new RebuildOptions { RemovePassword = true });

                    AssertLedger(db, "11=before-alpha", "29=before-omega");
                    InsertAfterRebuild(db);
                }

                File.ReadAllBytes(file)[0].Should().Be(0,
                    "RemovePassword is the explicit request to decrypt");

                AssertRejectedOpensDidNotChangeFile(file, () =>
                    AssertRejected(file, OldPassword, LiteException.NOT_ENCRYPTED));

                using (var reopened = Open(file, null))
                {
                    AssertCompleteLedger(reopened);
                }
            });
        }

        [Theory]
        [InlineData(false, ConnectionType.Direct)]
        [InlineData(false, ConnectionType.Shared)]
        [InlineData(true, ConnectionType.Direct)]
        [InlineData(true, ConnectionType.Shared)]
        public void Bare_sql_rebuild_is_parameterless_and_never_dereferences_null(
            bool encrypted,
            ConnectionType connection)
        {
            RunWithDatabase(file =>
            {
                var password = encrypted ? OldPassword : null;
                CreateDatabase(file, password);

                using (var db = Open(file, password, connection))
                {
                    var failure = Record.Exception(() =>
                    {
                        using var reader = db.Execute("REBUILD");
                        reader.ToArray();
                    });

                    failure.Should().BeNull("bare REBUILD must use the current engine settings");

                    AssertLedger(db, "11=before-alpha", "29=before-omega");
                    InsertAfterRebuild(db);
                }

                File.ReadAllBytes(file)[0].Should().Be(encrypted ? (byte)1 : (byte)0);

                if (encrypted)
                {
                    AssertRejectedOpensDidNotChangeFile(file, () =>
                        AssertRejected(file, null));
                }

                using (var reopened = Open(file, password))
                {
                    AssertCompleteLedger(reopened);
                }
            });
        }

        private static void CreateDatabase(string file, string password)
        {
            using var db = Open(file, password);
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["_id"] = 29, ["receipt"] = "before-omega" });
            rows.Insert(new BsonDocument { ["_id"] = 11, ["receipt"] = "before-alpha" });
            db.Checkpoint();
        }

        private static void InsertAfterRebuild(LiteDatabase db)
        {
            db.GetCollection("rows").Insert(
                new BsonDocument { ["_id"] = 47, ["receipt"] = "after-rebuild" });
            db.Checkpoint();
            AssertCompleteLedger(db);
        }

        private static void AssertCompleteLedger(LiteDatabase db)
        {
            AssertLedger(db, "11=before-alpha", "29=before-omega", "47=after-rebuild");
        }

        private static void AssertLedger(LiteDatabase db, params string[] expected)
        {
            var actual = db.GetCollection("rows")
                .Find(Query.All("_id"))
                .Select(x => $"{x["_id"].AsInt32}={x["receipt"].AsString}")
                .ToArray();

            actual.Should().Equal(expected);
        }

        private static void AssertRejected(string file, string password, int? expectedErrorCode = null)
        {
            Action openAndRead = () =>
            {
                using var db = Open(file, password);
                db.GetCollection("rows").FindById(11);
            };

            var failure = openAndRead.Should().Throw<LiteException>().Which;

            if (expectedErrorCode.HasValue)
            {
                failure.ErrorCode.Should().Be(expectedErrorCode.Value);
            }
        }

        private static void AssertRejectedOpensDidNotChangeFile(string file, Action rejectedOpen)
        {
            var bytesBefore = File.ReadAllBytes(file);
            rejectedOpen();
            File.ReadAllBytes(file).Should().Equal(bytesBefore,
                "a rejected password is a read-only probe and must not rewrite the database");
        }

        private static LiteDatabase Open(
            string file,
            string password,
            ConnectionType connection = ConnectionType.Direct)
        {
            return new LiteDatabase(new ConnectionString
            {
                Filename = file,
                Password = password,
                Connection = connection
            });
        }

        private static void RunWithDatabase(Action<string> test)
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "litedb-2824-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            try
            {
                test(Path.Combine(directory, "data.db"));
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
