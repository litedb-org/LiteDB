using System;
using System.IO;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2211_Tests
    {
        [Fact]
        public void ConnectionString_Should_Treat_Path_With_Equal_Sign_As_Filename()
        {
            var filename = GetTempDatabasePathWithEqualSign();

            try
            {
                var connectionString = new ConnectionString(filename);

                Assert.Equal(filename, connectionString.Filename);
            }
            finally
            {
                DeleteDatabaseFiles(filename);
            }
        }

        [Fact]
        public void LiteDatabase_Should_Open_File_Path_With_Equal_Sign()
        {
            var filename = GetTempDatabasePathWithEqualSign();

            try
            {
                using (var db = new LiteDatabase(filename))
                {
                    var collection = db.GetCollection<Data>("data");

                    Assert.Equal(0, collection.Count());
                }
            }
            finally
            {
                DeleteDatabaseFiles(filename);
            }
        }

        [Fact]
        public void ConnectionString_Should_Still_Parse_Filename_Key()
        {
            var connectionString = new ConnectionString("filename=sample=1.db");

            Assert.Equal("sample=1.db", connectionString.Filename);
        }

        [Fact]
        public void ConnectionString_Should_Parse_Custom_Key_Before_Filename_Key()
        {
            var connectionString = new ConnectionString("tenant=acme;filename=sample=1.db;readonly=true");

            Assert.Equal("sample=1.db", connectionString.Filename);
            Assert.True(connectionString.ReadOnly);
            Assert.Equal("acme", connectionString["tenant"]);
        }

        [Theory]
        [InlineData("filename=encrypted.db;password=abc=def;readonly=")]
        [InlineData("tenant=acme;readonly=")]
        [InlineData(@"C:\data\my.db;password=secret")]
        [InlineData("data/my.db;readonly=true")]
        [InlineData("filename=encrypted.db;bogus;readonly=true")]
        [InlineData("filename=encrypted.db;;readonly=true")]
        [InlineData("filename=encrypted.db;bogus")]
        [InlineData("filename=encrypted.db;=true")]
        public void Malformed_ConnectionString_Should_Not_Become_A_Filename(string malformed)
        {
            // A typo in an option must be reported. Treating the whole input as
            // a filename can silently create a database with that bizarre name.
            var exception = Record.Exception(() => new ConnectionString(malformed));

            Assert.NotNull(exception);
        }

        [Fact]
        public void ConnectionString_Should_Parse_Custom_Options_Without_A_BuiltIn_Option()
        {
            var connectionString = new ConnectionString("tenant=acme;region=west");

            Assert.Equal("acme", connectionString["tenant"]);
            Assert.Equal("west", connectionString["region"]);
            Assert.Equal(string.Empty, connectionString.Filename);
        }

        [Theory]
        [InlineData("memory profile", "LowMemory")]
        [InlineData("cache size", "64MB")]
        [InlineData("transaction pages", "32")]
        [InlineData(" MEMORY PROFILE ", "LowMemory")]
        [InlineData(" CACHE SIZE ", "64MB")]
        [InlineData(" TRANSACTION PAGES ", "32")]
        public void ConnectionString_Should_Preserve_A_Single_Memory_Option(string key, string value)
        {
            var connectionString = new ConnectionString(key + "=" + value);

            Assert.Equal(string.Empty, connectionString.Filename);
            Assert.Equal(value, connectionString[key.Trim()]);
            Assert.Throws<ArgumentException>(() =>
            {
                using (var db = new LiteDatabase(connectionString))
                {
                    db.GetCollection<Data>("data").Count();
                }
            });
        }

        [Theory]
        [InlineData("test=1.LiteDB")]
        [InlineData("data/test=1.db")]
        [InlineData("data=1/test.db")]
        [InlineData(@"C:\data\test=1.db")]
        [InlineData(@"\\server\data=1\test.db")]
        [InlineData("./password=secret.db")]
        [InlineData("tenant=acme")]
        [InlineData("filenam=production.db")]
        public void ConnectionString_Should_Treat_Unknown_Single_Key_Input_As_A_Path(string filename)
        {
            var connectionString = new ConnectionString(filename);

            Assert.Equal(filename, connectionString.Filename);
            Assert.Null(connectionString.Password);
        }

        [Fact]
        public void ConnectionString_Should_Parse_Quoted_Filename_And_Options()
        {
            var connectionString = new ConnectionString(
                "filename=\"data/my=1;archive.db\";password=\"abc=def;ghi\";readonly=true");

            Assert.Equal("data/my=1;archive.db", connectionString.Filename);
            Assert.Equal("abc=def;ghi", connectionString.Password);
            Assert.True(connectionString.ReadOnly);
        }

        [Theory]
        [InlineData("password=secret.db")]
        [InlineData("data/my=1;archive.db")]
        public void ConnectionString_Should_Accept_An_Explicit_Filename_Without_Parsing(string filename)
        {
            var connectionString = new ConnectionString { Filename = filename };

            Assert.Equal(filename, connectionString.Filename);
            Assert.Null(connectionString.Password);
        }

        [Theory]
        [InlineData("data.db", "password=secret", typeof(FormatException))]
        [InlineData("data.db", "readonly=true", typeof(FormatException))]
        [InlineData("data=1.db", "password=secret", typeof(ArgumentException))]
        [InlineData("data=1.db", "readonly=true", typeof(ArgumentException))]
        public void LiteDatabase_Should_Reject_Path_Plus_Options_Without_Creating_Files(
            string filename, string option, Type exceptionType)
        {
            var directory = Path.Combine(Path.GetTempPath(), "litedb-2211-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            try
            {
                var malformed = Path.Combine(directory, filename) + ";" + option;

                Assert.Throws(exceptionType, () =>
                {
                    using (var db = new LiteDatabase(malformed))
                    {
                        db.GetCollection<Data>("data").Count();
                    }
                });
                Assert.Empty(Directory.GetFiles(directory));
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Fact]
        public void ConnectionString_Should_Allow_Whitespace_After_Trailing_Separator()
        {
            var connectionString = new ConnectionString("filename=sample=1.db;readonly=true; \t");

            Assert.Equal("sample=1.db", connectionString.Filename);
            Assert.True(connectionString.ReadOnly);
        }

        [Fact]
        public void ConnectionString_Should_Preserve_Empty_Password_Compatibility()
        {
            var connectionString = new ConnectionString("filename=sample=1.db;password=");

            Assert.Equal("sample=1.db", connectionString.Filename);
            Assert.Null(connectionString.Password);
        }

        private static string GetTempDatabasePathWithEqualSign()
        {
            var filename = "litedb-" + Guid.NewGuid().ToString("d").Substring(0, 5) + "=issue2211.db";

            return Path.Combine(Path.GetTempPath(), filename);
        }

        private static void DeleteDatabaseFiles(string filename)
        {
            DeleteIfExists(filename);
            DeleteIfExists(Path.Combine(
                Path.GetDirectoryName(filename),
                Path.GetFileNameWithoutExtension(filename) + "-log" + Path.GetExtension(filename)));
            DeleteIfExists(Path.Combine(
                Path.GetDirectoryName(filename),
                Path.GetFileNameWithoutExtension(filename) + "-tmp" + Path.GetExtension(filename)));
        }

        private static void DeleteIfExists(string filename)
        {
            if (File.Exists(filename))
            {
                File.Delete(filename);
            }
        }

        public class Data
        {
            public int Id { get; set; }
        }
    }
}
