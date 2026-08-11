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

        [Fact]
        public void Malformed_ConnectionString_Should_Not_Become_A_Filename()
        {
            const string malformed = "filename=encrypted.db;password=abc=def;readonly=";

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
