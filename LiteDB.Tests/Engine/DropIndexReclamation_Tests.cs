using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Utils;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class DropIndexReclamation_Tests
    {
        private static readonly FieldInfo EngineField = typeof(LiteDatabase)
            .GetField("_engine", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly MethodInfo AutoTransactionMethod = typeof(LiteEngine)
            .GetMethod("AutoTransaction", BindingFlags.NonPublic | BindingFlags.Instance);

        [Fact]
        public void DropIndex_Reclaims_Sentinel_Only_Page_Before_Collection_Drop()
        {
            using var file = new TempFile();
            uint indexPageID;

            using (var db = DatabaseFactory.Create(TestDatabaseType.Disk, file.Filename))
            {
                var collection = db.GetCollection("docs");
                collection.Insert(new BsonDocument { ["_id"] = 1, ["Value"] = 1 });
                collection.EnsureIndex("value", "Value");
                collection.Delete(1);
                indexPageID = Execute(db, transaction => transaction.CreateSnapshot(LockMode.Read, "docs", false)
                    .CollectionPage.GetCollectionIndex("value").Head.PageID);

                collection.DropIndex("value").Should().BeTrue();
                db.DropCollection("docs").Should().BeTrue();
                db.Checkpoint();
            }

            using var reopened = DatabaseFactory.Create(TestDatabaseType.Disk, file.Filename);
            GetPageTypes(reopened, new[] { indexPageID })[indexPageID].Should().Be(PageType.Empty);
            reopened.GetCollectionNames().Should().NotContain("docs");
        }

        [Fact]
        public void DropIndex_Reclaims_All_Multikey_Pages_After_Safepoints()
        {
            using var file = new TempFile();
            using var db = DatabaseFactory.Create(TestDatabaseType.Disk,
                $"Filename={file.Filename};Transaction Pages=4");
            var collection = db.GetCollection("docs");
            collection.InsertBulk(Enumerable.Range(1, 300).Select(id => new BsonDocument
            {
                ["_id"] = id,
                ["Tags"] = new BsonArray($"tag-{id:D4}-{new string('x', 180)}", $"group-{id % 11}")
            }));
            collection.EnsureIndex("tags", "Tags[*]");

            var pageIDs = Execute(db, transaction =>
            {
                var snapshot = transaction.CreateSnapshot(LockMode.Read, "docs", false);
                var index = snapshot.CollectionPage.GetCollectionIndex("tags");
                return new IndexService(snapshot, Collation.Binary, snapshot.MaxItemsCount)
                    .FindAll(index, Query.Ascending).Select(node => node.Page.PageID).Distinct().ToArray();
            });

            pageIDs.Should().HaveCountGreaterThan(1);
            collection.DropIndex("tags").Should().BeTrue();
            db.Checkpoint();
            GetPageTypes(db, pageIDs).Values.Should().OnlyContain(type => type == PageType.Empty);
        }

        private static T Execute<T>(LiteDatabase db, Func<TransactionService, T> action)
        {
            var engine = (LiteEngine)EngineField.GetValue(db);
            return (T)AutoTransactionMethod.MakeGenericMethod(typeof(T)).Invoke(engine, new object[] { action });
        }

        private static Dictionary<uint, PageType> GetPageTypes(LiteDatabase db, IEnumerable<uint> pageIDs) =>
            Execute(db, transaction =>
            {
                var snapshot = transaction.CreateSnapshot(LockMode.Read, "$", false);
                return pageIDs.Distinct().ToDictionary(id => id, id => snapshot.GetPage<BasePage>(id).PageType);
            });
    }
}
