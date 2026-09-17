using System;
using System.IO;
using System.Linq;

using LiteDB.Generated;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LiteDB.AotTests
{
    public sealed partial class SourceGeneratedMappingTests
    {
        [TestMethod]
        public void GetGeneratedCollection_GroupBy_DeserializesGeneratedComplexKeys()
        {
            var path = GetDatabasePath();

            try
            {
                var mapper = new ThrowingConversionMapper();
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<PhaseCScalarRecord>("complexGroupingKeys");
                collection.Insert(new[]
                {
                    new PhaseCScalarRecord { Name = "first", Score = 1 },
                    new PhaseCScalarRecord { Name = "second", Score = 2 }
                });

                var groups = collection.Query()
                    .GroupBy(record => new PhaseCScalarRecord
                    {
                        Name = record.Name,
                        Score = record.Score
                    })
                    .ToArray();

                Assert.AreEqual(2, groups.Length);
                CollectionAssert.AreEquivalent(new[] { "first", "second" }, groups.Select(group => group.Key.Name).ToArray());
                CollectionAssert.AreEquivalent(new[] { 1, 2 }, groups.Select(group => group.Key.Score).ToArray());
                Assert.IsTrue(groups.All(group => group.Count() == 1));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void GetGeneratedCollection_GroupBy_RejectsUnmappedComplexKeysBeforeExecution()
        {
            var path = GetDatabasePath();

            try
            {
                var mapper = new ThrowingConversionMapper();
                LiteDbGeneratedMappings.Register(mapper);

                using var database = new LiteDatabase(path, mapper);
                var collection = database.GetGeneratedCollection<PhaseCScalarRecord>("unmappedGroupingKeys");

                var exception = Assert.ThrowsException<NotSupportedException>(() =>
                    collection.Query().GroupBy(record => new { record.Name, record.Score }));

                StringAssert.Contains(exception.Message, "requires a registered generated execution map");
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
