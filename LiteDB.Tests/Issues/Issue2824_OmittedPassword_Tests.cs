using System;
using System.IO;
using System.Linq;
using System.Text;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2824_OmittedPassword_Tests
    {
        private const string OldPassword = "old-password-2824";
        private const string NewPassword = "new-password-2824";
        private const string Marker = "plaintext-marker-2824";
        private const string FileId = "files/marker.txt";

        [Theory]
        [InlineData(ConnectionType.Direct, "api-collation")]
        [InlineData(ConnectionType.Shared, "api-collation")]
        [InlineData(ConnectionType.Direct, "api-error-report")]
        [InlineData(ConnectionType.Direct, "api-empty")]
        [InlineData(ConnectionType.Direct, "sql-collation")]
        [InlineData(ConnectionType.Shared, "sql-collation")]
        [InlineData(ConnectionType.Direct, "sql-empty")]
        [InlineData(ConnectionType.Direct, "sql-null-password")]
        public void Options_without_a_password_keep_an_encrypted_database_encrypted(
            ConnectionType connection,
            string shape)
        {
            RunWithDatabase(file =>
            {
                CreateDatabase(file, OldPassword);

                using (var db = Open(file, OldPassword, connection))
                {
                    Rebuild(db, shape);

                    AssertContent(db);
                }

                AssertEncrypted(file);
                AssertRejected(file, null);
                AssertRejected(file, NewPassword);

                using (var reopened = Open(file, OldPassword))
                {
                    AssertContent(reopened);
                }
            });
        }

        [Theory]
        [InlineData(ConnectionType.Direct, "api-remove")]
        [InlineData(ConnectionType.Shared, "api-remove")]
        [InlineData(ConnectionType.Direct, "sql-remove")]
        [InlineData(ConnectionType.Shared, "sql-remove")]
        public void Explicit_removal_produces_a_plain_database(ConnectionType connection, string shape)
        {
            RunWithDatabase(file =>
            {
                CreateDatabase(file, OldPassword);

                using (var db = Open(file, OldPassword, connection))
                {
                    Rebuild(db, shape);

                    AssertContent(db);
                }

                AssertPlain(file);
                AssertRejected(file, OldPassword);

                using (var reopened = Open(file, null))
                {
                    AssertContent(reopened);
                }
            });
        }

        [Theory]
        [InlineData(ConnectionType.Direct, "api-change")]
        [InlineData(ConnectionType.Direct, "sql-change")]
        [InlineData(ConnectionType.Shared, "sql-change")]
        public void A_supplied_password_replaces_the_current_one(ConnectionType connection, string shape)
        {
            RunWithDatabase(file =>
            {
                CreateDatabase(file, OldPassword);

                using (var db = Open(file, OldPassword, connection))
                {
                    Rebuild(db, shape);

                    AssertContent(db);
                }

                AssertEncrypted(file);
                AssertRejected(file, null);
                AssertRejected(file, OldPassword);

                using (var reopened = Open(file, NewPassword))
                {
                    AssertContent(reopened);
                }
            });
        }

        [Theory]
        [InlineData("api-collation", false)]
        [InlineData("api-empty", false)]
        [InlineData("api-remove", false)]
        [InlineData("sql-collation", false)]
        [InlineData("sql-null-password", false)]
        [InlineData("sql-remove", false)]
        [InlineData("api-change", true)]
        [InlineData("sql-change", true)]
        public void A_plain_database_is_encrypted_only_by_a_supplied_password(string shape, bool encrypted)
        {
            RunWithDatabase(file =>
            {
                CreateDatabase(file, null);

                using (var db = Open(file, null))
                {
                    Rebuild(db, shape);

                    AssertContent(db);
                }

                if (encrypted)
                {
                    AssertEncrypted(file);
                    AssertRejected(file, null);
                }
                else
                {
                    AssertPlain(file);
                }

                using (var reopened = Open(file, encrypted ? NewPassword : null))
                {
                    AssertContent(reopened);
                }
            });
        }

        [Fact]
        public void An_empty_password_is_still_a_password_and_not_a_removal()
        {
            RunWithDatabase(file =>
            {
                CreateDatabase(file, OldPassword);

                using (var db = Open(file, OldPassword))
                {
                    db.Rebuild(new RebuildOptions { Password = string.Empty });

                    AssertContent(db);
                }

                AssertEncrypted(file);
                AssertRejected(file, null);

                using (var reopened = Open(file, string.Empty))
                {
                    AssertContent(reopened);
                }
            });
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Removing_and_setting_a_password_at_once_is_rejected_before_the_file_is_touched(bool sql)
        {
            RunWithDatabase(file =>
            {
                CreateDatabase(file, OldPassword);

                var bytesBefore = File.ReadAllBytes(file);

                using (var db = Open(file, OldPassword))
                {
                    Action rebuild = () =>
                    {
                        if (sql)
                        {
                            using var reader = db.Execute(
                                $"REBUILD {{ password: '{NewPassword}', removePassword: true }}");
                            reader.ToArray();
                        }
                        else
                        {
                            db.Rebuild(new RebuildOptions { Password = NewPassword, RemovePassword = true });
                        }
                    };

                    rebuild.Should().Throw<ArgumentException>();

                    AssertContent(db);
                }

                File.ReadAllBytes(file).Should().Equal(bytesBefore);
                Directory.GetFiles(Path.GetDirectoryName(file)).Should().Equal(file);
            });
        }

        private static void Rebuild(LiteDatabase db, string shape)
        {
            var collation = new Collation("en-US/IgnoreCase");

            switch (shape)
            {
                case "api-collation":
                    db.Rebuild(new RebuildOptions { Collation = collation });
                    db.Collation.ToString().Should().Be(collation.ToString());
                    break;
                case "api-error-report":
                    db.Rebuild(new RebuildOptions { IncludeErrorReport = true });
                    break;
                case "api-empty":
                    db.Rebuild(new RebuildOptions());
                    break;
                case "api-remove":
                    db.Rebuild(new RebuildOptions { RemovePassword = true });
                    break;
                case "api-change":
                    db.Rebuild(new RebuildOptions { Password = NewPassword });
                    break;
                case "sql-collation":
                    Execute(db, "REBUILD { collation: 'en-US/IgnoreCase' }");
                    db.Collation.ToString().Should().Be(collation.ToString());
                    break;
                case "sql-empty":
                    Execute(db, "REBUILD {}");
                    break;
                case "sql-null-password":
                    Execute(db, "REBUILD { password: null }");
                    break;
                case "sql-remove":
                    Execute(db, "REBUILD { removePassword: true }");
                    break;
                case "sql-change":
                    Execute(db, $"REBUILD {{ password: '{NewPassword}' }}");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
            }
        }

        private static void Execute(LiteDatabase db, string sql)
        {
            using var reader = db.Execute(sql);
            reader.ToArray();
        }

        private static void CreateDatabase(string file, string password)
        {
            using var db = Open(file, password);
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["_id"] = 29, ["receipt"] = Marker });
            rows.Insert(new BsonDocument { ["_id"] = 11, ["receipt"] = "before-alpha" });
            rows.EnsureIndex("receipt", true);

            using (var content = new MemoryStream(Encoding.UTF8.GetBytes(Marker)))
            {
                db.FileStorage.Upload(FileId, "marker.txt", content);
            }

            db.Checkpoint();
        }

        private static void AssertContent(LiteDatabase db)
        {
            var rows = db.GetCollection("rows");

            rows.Find(Query.All("_id"))
                .Select(x => $"{x["_id"].AsInt32}={x["receipt"].AsString}")
                .Should().Equal("11=before-alpha", "29=" + Marker);

            rows.FindOne(Query.EQ("receipt", Marker))["_id"].AsInt32.Should().Be(29);

            db.GetCollection("$indexes")
                .Find(Query.EQ("collection", "rows"))
                .Select(x => x["name"].AsString)
                .Should().Contain("receipt");

            using (var content = new MemoryStream())
            {
                db.FileStorage.Download(FileId, content);
                Encoding.UTF8.GetString(content.ToArray()).Should().Be(Marker);
            }
        }

        private static void AssertEncrypted(string file)
        {
            var bytes = File.ReadAllBytes(file);

            bytes[0].Should().Be(1, "the rebuilt data file must stay encrypted");
            ContainsMarker(bytes).Should().BeFalse("an encrypted file must not expose stored values");
        }

        private static void AssertPlain(string file)
        {
            var bytes = File.ReadAllBytes(file);

            bytes[0].Should().Be(0);
            ContainsMarker(bytes).Should().BeTrue("the marker proves the probe can see plaintext values");
        }

        private static bool ContainsMarker(byte[] bytes)
        {
            // Latin1 maps every byte to one char, so an ordinal search is a raw byte search.
            var raw = Encoding.GetEncoding("ISO-8859-1").GetString(bytes);

            return raw.IndexOf(Marker, StringComparison.Ordinal) >= 0;
        }

        private static void AssertRejected(string file, string password)
        {
            Action openAndRead = () =>
            {
                using var db = Open(file, password);
                db.GetCollection("rows").FindById(11);
            };

            openAndRead.Should().Throw<LiteException>();
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
                "litedb-2824-omitted-" + Guid.NewGuid().ToString("N"));
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
