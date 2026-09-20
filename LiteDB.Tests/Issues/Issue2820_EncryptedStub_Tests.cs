using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    /// <summary>
    /// #2820 must not take away #2343: an encryption preamble followed only by zeros holds no user data.
    /// </summary>
    public class Issue2820_EncryptedStub_Tests
    {
        private const int PageSize = 8192;
        private const int SaltEnd = 17;
        private const string Password = "abc";

        [Theory]
        [InlineData(SaltEnd)]
        [InlineData(64)]
        [InlineData(5000)]
        [InlineData(PageSize)]
        public void An_interrupted_encrypted_creation_opens_and_becomes_a_working_database(int length)
        {
            using var file = new UniqueFile();
            var stub = Stub(length);
            File.WriteAllBytes(file.Filename, stub);

            using (var db = Open(file.Filename, Password))
            {
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "kept" });
            }

            using (var db = Open(file.Filename, Password))
            {
                db.GetCollection("rows").FindById(1)["value"].AsString.Should().Be("kept");
            }

            File.ReadAllBytes(file.Filename).Take(SaltEnd).Should().Equal(stub.Take(SaltEnd),
                "completing the preamble must only fill bytes that were zero");
        }

        [Theory]
        [InlineData(64)]
        [InlineData(5000)]
        public void A_preamble_cut_after_its_check_block_opens_only_with_its_own_password(int length)
        {
            using var file = new UniqueFile();
            using (Open(file.Filename, Password))
            {
            }
            var preamble = File.ReadAllBytes(file.Filename).Take(length).ToArray();
            File.WriteAllBytes(file.Filename, preamble);

            AssertRejectedUnchanged(file.Filename, "another-password", false);

            using (var db = Open(file.Filename, Password))
            {
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "kept" });
            }

            using (var db = Open(file.Filename, Password))
            {
                db.GetCollection("rows").FindById(1)["value"].AsString.Should().Be("kept");
            }

            File.ReadAllBytes(file.Filename).Take(64).Should().Equal(preamble.Take(64));
        }

        [Fact]
        public void An_interrupted_encrypted_log_does_not_block_the_database()
        {
            using var file = new UniqueFile();
            using (var db = Open(file.Filename, Password))
            {
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "kept" });
            }
            File.WriteAllBytes(LogName(file.Filename), Stub(PageSize));

            using (var db = Open(file.Filename, Password))
            {
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2, ["value"] = "after" });
                db.GetCollection("rows").Count().Should().Be(2);
            }
        }

        [Fact]
        public void A_recovered_stub_rejects_a_different_password_without_changing_the_file()
        {
            using var file = new UniqueFile();
            File.WriteAllBytes(file.Filename, Stub(PageSize));
            using (var db = Open(file.Filename, Password))
            {
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            }

            AssertRejectedUnchanged(file.Filename, "another-password", false);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_real_encrypted_database_rejects_a_wrong_password_without_changing_the_file(bool readOnly)
        {
            using var file = new UniqueFile();
            using (var db = Open(file.Filename, Password))
            {
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            }

            AssertRejectedUnchanged(file.Filename, "another-password", readOnly);
        }

        [Theory]
        [InlineData(100, (byte)'N')]
        [InlineData(100, (byte)1)]
        [InlineData(16, (byte)1)]
        [InlineData(PageSize, (byte)'N')]
        [InlineData(PageSize, (byte)1)]
        public void A_foreign_file_opened_with_a_password_is_rejected_without_changing_the_file(int length, byte first)
        {
            using var file = new UniqueFile();
            var bytes = new byte[length];
            new Random(2820).NextBytes(bytes);
            bytes[0] = first;
            File.WriteAllBytes(file.Filename, bytes);

            AssertRejectedUnchanged(file.Filename, Password, false);
            AssertRejectedUnchanged(file.Filename, Password, true);
        }

        [Theory]
        [InlineData(PageSize, 40)]
        [InlineData(PageSize, 4000)]
        [InlineData(PageSize, PageSize - 1)]
        [InlineData(5000, 4999)]
        [InlineData(PageSize * 3, PageSize * 2 + 5)]
        public void A_stub_followed_by_any_content_is_never_reinitialised(int length, int contentAt)
        {
            using var file = new UniqueFile();
            var bytes = Stub(length);
            bytes[contentAt] = 0x5A;
            File.WriteAllBytes(file.Filename, bytes);

            AssertRejectedUnchanged(file.Filename, Password, false);
            AssertRejectedUnchanged(file.Filename, Password, true);
        }

        [Theory]
        [InlineData(SaltEnd)]
        [InlineData(PageSize)]
        public void Read_only_open_of_a_stub_does_not_write(int length)
        {
            using var file = new UniqueFile();
            File.WriteAllBytes(file.Filename, Stub(length));

            AssertRejectedUnchanged(file.Filename, Password, true);
        }

        private static void AssertRejectedUnchanged(string filename, string password, bool readOnly)
        {
            var before = File.ReadAllBytes(filename);

            var failure = Record.Exception(() =>
            {
                using var db = new LiteDatabase(new ConnectionString
                {
                    Filename = filename,
                    Password = password,
                    ReadOnly = readOnly
                });
                db.GetCollectionNames().ToArray();
            });

            failure.Should().BeOfType<LiteException>();
            File.ReadAllBytes(filename).Should().Equal(before);
            File.Exists(LogName(filename)).Should().BeFalse("a rejected open must not leave a log file behind");
        }

        private static LiteDatabase Open(string filename, string password)
        {
            return new LiteDatabase(new ConnectionString { Filename = filename, Password = password });
        }

        private static string LogName(string filename)
        {
            return Path.Combine(Path.GetDirectoryName(filename),
                Path.GetFileNameWithoutExtension(filename) + "-log" + Path.GetExtension(filename));
        }

        /// <summary>
        /// Full-GUID names: the log-file assertions must not see leftovers of a colliding short temp name.
        /// </summary>
        private sealed class UniqueFile : IDisposable
        {
            public string Filename { get; } = Path.Combine(Path.GetTempPath(), "litedb-2820-" + Guid.NewGuid().ToString("n") + ".db");

            public void Dispose()
            {
                File.Delete(this.Filename);
                File.Delete(LogName(this.Filename));
            }
        }

        private static byte[] Stub(int length)
        {
            var bytes = new byte[length];
            bytes[0] = 1;
            for (var i = 1; i < SaltEnd; i++) bytes[i] = (byte)(i * 13 + 7);
            return bytes;
        }
    }
}
