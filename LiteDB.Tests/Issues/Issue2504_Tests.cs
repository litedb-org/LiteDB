using System;
using System.IO;
using System.Linq;

using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Person
    {
        [BsonId]
        public int Id { get; set; }
        public string Name { get; set; }

        public Person(int id, string name)
        {
            Id = id;
            Name = name;
        }
    }

    public class Issue2504_Tests
    {
        private static string CreateCorruptedDatabase()
        {
            using var memoryStream = new MemoryStream();
            using (var db = new LiteDatabase(memoryStream))
            {
                var col1 = db.GetCollection<Person>("col1");
                col1.Insert(new Person(1, "Alpha"));
                var col2 = db.GetCollection<Person>("col2");
                col2.Insert(new Person(2, "Beta"));
                db.DropCollection("col2");
            }

            // 2) Zmień typ wszystkich pustych stron na Data (4)
            var bytes = memoryStream.ToArray();
            const int pageSize = 8192;
            const int PAGE_TYPE_OFFSET = 4;
            const byte PAGE_TYPE_EMPTY = 0;
            const byte PAGE_TYPE_DATA = 4;

            for (int offset = 0; offset + pageSize <= bytes.Length; offset += pageSize)
            {
                if (bytes[offset + PAGE_TYPE_OFFSET] == PAGE_TYPE_EMPTY)
                {
                    bytes[offset + PAGE_TYPE_OFFSET] = PAGE_TYPE_DATA;
                }
            }

            var tempPath = Path.Combine(Path.GetTempPath(), $"LiteDB_Issue2504_{Guid.NewGuid():N}.db");
            File.WriteAllBytes(tempPath, bytes);
            return tempPath;
        }

        [Fact]
        public void Recoverable_FreeList_Is_Repaired_With_AutoRebuild_Disabled()
        {
            var dbPath = CreateCorruptedDatabase();
            var backupPath = Path.Combine(Path.GetDirectoryName(dbPath), Path.GetFileNameWithoutExtension(dbPath) + "-backup.db");

            try
            {
                using var db = new LiteDatabase(dbPath);
                var col1 = db.GetCollection<Person>("col1");
                var bulk = Enumerable.Range(3, 5_000).Select(i => new Person(i, "Gamma"));
                var ex = Record.Exception(() => col1.InsertBulk(bulk));
                // Current dev validates and repairs this free list on allocation.
                Assert.Null(ex);
                Assert.Equal(5_001, col1.Count());
                Assert.False(File.Exists(backupPath));
            }
            finally
            {
                if (File.Exists(backupPath)) File.Delete(backupPath);
                if (File.Exists(dbPath)) File.Delete(dbPath);
            }
        }

        [Fact]
        public void Recoverable_FreeList_Does_Not_Require_AutoRebuild()
        {
            string dbPath = CreateCorruptedDatabase();
            string backupPath = Path.Combine(Path.GetDirectoryName(dbPath), Path.GetFileNameWithoutExtension(dbPath) + "-backup.db");
            try
            {
                using (var db = new LiteDatabase($"Filename={dbPath};AutoRebuild=true"))
                {
                    var col1 = db.GetCollection<Person>("col1");

                    var bulk = Enumerable.Range(3, 500).Select(i => new Person(i, "Gamma"));
                    col1.InsertBulk(bulk);

                    var allDocs = col1.Query().ToList();
                    Assert.Contains(allDocs, x => x.Name == "Alpha");
                    Assert.True(allDocs.Count >= 2);
                    Assert.False(db.CollectionExists("col2"));
                    if (db.CollectionExists("_rebuild_errors"))
                    {
                        var rebuildErrors = db.GetCollection<BsonDocument>("_rebuild_errors");
                        Assert.True(rebuildErrors.Count() > 0, "Rebuild errors should be logged due to corruption");
                    }
                }
                Assert.False(File.Exists(backupPath), "Lazy free-list repair should avoid a full rebuild");

            }
            finally
            {
                if (File.Exists(backupPath)) File.Delete(backupPath);
                if (File.Exists(dbPath)) File.Delete(dbPath);
            }
        }
    }
}
