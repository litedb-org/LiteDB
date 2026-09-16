using System;
using System.Globalization;
using System.Linq;
using FluentAssertions;
using LiteDB.Vector;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2805_Tests
    {
        public class Row
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public string IName { get; set; }
            public string 名称 { get; set; }
            public string 城市 { get; set; }
        }

        public class VectorRow
        {
            public int Id { get; set; }
            public float[] Embedding { get; set; }
            public float[] IEmbedding { get; set; }
            public float[] 向量 { get; set; }
            public float[] 向量二 { get; set; }
        }

        [Fact]
        public void Persisted_legacy_names_are_preserved_when_canonical_names_are_created()
        {
            using var file = new TempFile();
            var options = new VectorIndexOptions(2);
            var target = new float[] { 1, 0 };
            using (var db = new LiteDatabase(file.Filename))
            {
                var rows = db.GetCollection<Row>("rows");
                rows.Insert(new Row { Id = 1, IName = "hit" });
                // These are the names produced by the old Turkish-culture filter.
                rows.EnsureIndex("Name", x => x.IName).Should().BeTrue();
                ILiteCollection<VectorRow> vectors = db.GetCollection<VectorRow>("vectors");
                vectors.Insert(new VectorRow { Id = 1, IEmbedding = target });
                vectors.EnsureIndex("Embedding", x => x.IEmbedding, options).Should().BeTrue();
            }
            using (var db = new LiteDatabase(file.Filename))
            {
                var rows = db.GetCollection<Row>("rows");
                ILiteCollection<VectorRow> vectors = db.GetCollection<VectorRow>("vectors");
                rows.EnsureIndex(x => x.IName).Should().BeTrue();
                vectors.EnsureIndex(x => x.IEmbedding, options).Should().BeTrue();
                using (new CultureScope("tr-TR"))
                {
                    rows.EnsureIndex(x => x.IName).Should().BeFalse();
                    vectors.EnsureIndex(x => x.IEmbedding, options).Should().BeFalse();
                }
                AssertCatalog(db, "rows", ("_id", "$._id"), ("Name", "$.IName"), ("IName", "$.IName"));
                AssertCatalog(db, "vectors", ("_id", "$._id"),
                    ("Embedding", "$.IEmbedding"), ("IEmbedding", "$.IEmbedding"));
                rows.Find(x => x.IName == "hit").Select(x => x.Id).Should().Equal(1);
                vectors.Query().WhereNear(x => x.IEmbedding, target, 0.001)
                    .ToArray().Select(x => x.Id).Should().Equal(1);
                rows.DropIndex("Name").Should().BeTrue();
                vectors.DropIndex("Embedding").Should().BeTrue();
                rows.EnsureIndex(x => x.IName).Should().BeFalse();
                vectors.EnsureIndex(x => x.IEmbedding, options).Should().BeFalse();
            }
        }

        [Theory]
        [InlineData("")]
        [InlineData("tr-TR")]
        public void Scalar_generated_names_are_exact_unique_and_query_the_intended_fields(string culture)
        {
            using var cultureScope = new CultureScope(culture);
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection<Row>("rows");

            rows.Insert(new[]
            {
                CreateRow(1, "name-hit", "other", "其他", "别处"),
                CreateRow(2, "other", "iname-hit", "另外", "异地"),
                CreateRow(3, "else", "different", "名称-hit", "他处"),
                CreateRow(4, "different", "else", "无关", "城市-hit")
            });

            // Exercise both public auto-name overloads. Two Unicode fields make a
            // constant fallback collide, while Name/IName reproduce the Turkish-I collision.
            rows.EnsureIndex(x => x.Name).Should().BeTrue();
            rows.EnsureIndex(BsonExpression.Create("$.IName")).Should().BeTrue();
            rows.EnsureIndex(x => x.名称).Should().BeTrue();
            rows.EnsureIndex(BsonExpression.Create("$.城市")).Should().BeTrue();

            rows.EnsureIndex(x => x.Name).Should().BeFalse();
            rows.EnsureIndex(BsonExpression.Create("$.IName")).Should().BeFalse();
            rows.EnsureIndex(x => x.名称).Should().BeFalse();
            rows.EnsureIndex(BsonExpression.Create("$.城市")).Should().BeFalse();

            AssertCatalog(db, "rows",
                ("_id", "$._id"),
                ("Name", "$.Name"),
                ("IName", "$.IName"),
                ("名称", "$.名称"),
                ("城市", "$.城市"));

            AssertScalarQuery(rows.Query().Where(x => x.Name == "name-hit"), "Name", "$.Name", 1);
            AssertScalarQuery(rows.Query().Where("$.IName = @0", "iname-hit"), "IName", "$.IName", 2);
            AssertScalarQuery(rows.Query().Where(x => x.名称 == "名称-hit"), "名称", "$.名称", 3);
            AssertScalarQuery(rows.Query().Where("$.城市 = @0", "城市-hit"), "城市", "$.城市", 4);

            // A raw-document read is independent of the typed mapper and proves the
            // query results were not manufactured by remapping one CLR property.
            db.GetCollection("rows").FindAll()
                .OrderBy(x => x["_id"].AsInt32)
                .Select(x => $"{x["_id"].AsInt32}:{x["Name"].AsString}:{x["IName"].AsString}:{x["名称"].AsString}:{x["城市"].AsString}")
                .Should().Equal(
                    "1:name-hit:other:其他:别处",
                    "2:other:iname-hit:另外:异地",
                    "3:else:different:名称-hit:他处",
                    "4:different:else:无关:城市-hit");
        }

        [Fact]
        public void Generated_name_is_idempotent_when_culture_changes()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection<Row>("rows");

            using (new CultureScope(""))
            {
                rows.EnsureIndex(x => x.IName).Should().BeTrue();
            }

            using (new CultureScope("tr-TR"))
            {
                rows.EnsureIndex(x => x.IName).Should().BeFalse(
                    "the same expression must not acquire a second name after a culture change");
            }

            AssertCatalog(db, "rows", ("_id", "$._id"), ("IName", "$.IName"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("tr-TR")]
        public void Vector_generated_names_are_exact_unique_and_select_the_right_indexes(string culture)
        {
            using var cultureScope = new CultureScope(culture);
            using var db = new LiteDatabase(":memory:");
            ILiteCollection<VectorRow> vectors = db.GetCollection<VectorRow>("vectors");
            var a = new[] { 1f, 0f };
            var b = new[] { 0f, 1f };
            var options = new VectorIndexOptions(2, VectorDistanceMetric.Cosine);

            vectors.Insert(new[]
            {
                CreateVectorRow(1, a, b, b, b),
                CreateVectorRow(2, b, a, b, b),
                CreateVectorRow(3, b, b, a, b),
                CreateVectorRow(4, b, b, b, a)
            });

            // Use extension methods through ILiteCollection so this covers the public
            // vector API rather than the obsolete concrete-collection shims.
            vectors.EnsureIndex(x => x.Embedding, options).Should().BeTrue();
            vectors.EnsureIndex(BsonExpression.Create("$.IEmbedding"), options).Should().BeTrue();
            vectors.EnsureIndex(x => x.向量, options).Should().BeTrue();
            vectors.EnsureIndex(BsonExpression.Create("$.向量二"), options).Should().BeTrue();

            vectors.EnsureIndex(x => x.Embedding, options).Should().BeFalse();
            vectors.EnsureIndex(BsonExpression.Create("$.IEmbedding"), options).Should().BeFalse();
            vectors.EnsureIndex(x => x.向量, options).Should().BeFalse();
            vectors.EnsureIndex(BsonExpression.Create("$.向量二"), options).Should().BeFalse();

            AssertCatalog(db, "vectors",
                ("_id", "$._id"),
                ("Embedding", "$.Embedding"),
                ("IEmbedding", "$.IEmbedding"),
                ("向量", "$.向量"),
                ("向量二", "$.向量二"));

            AssertVectorQuery(vectors.Query().WhereNear(x => x.Embedding, a, 0.001), "Embedding", "$.Embedding", 1);
            AssertVectorQuery(vectors.Query().WhereNear(BsonExpression.Create("$.IEmbedding"), a, 0.001), "IEmbedding", "$.IEmbedding", 2);
            AssertVectorQuery(vectors.Query().WhereNear(x => x.向量, a, 0.001), "向量", "$.向量", 3);
            AssertVectorQuery(vectors.Query().WhereNear(BsonExpression.Create("$.向量二"), a, 0.001), "向量二", "$.向量二", 4);
        }

        private static Row CreateRow(int id, string name, string iName, string chineseName, string city)
        {
            return new Row { Id = id, Name = name, IName = iName, 名称 = chineseName, 城市 = city };
        }

        private static VectorRow CreateVectorRow(int id, float[] embedding, float[] iEmbedding, float[] vector, float[] vectorTwo)
        {
            return new VectorRow { Id = id, Embedding = embedding, IEmbedding = iEmbedding, 向量 = vector, 向量二 = vectorTwo };
        }

        private static void AssertScalarQuery(ILiteQueryable<Row> query, string name, string expression, int id)
        {
            AssertPlan(query.GetPlan(), name, expression, "INDEX SEEK");
            query.ToArray().Select(x => x.Id).Should().Equal(id);
        }

        private static void AssertVectorQuery(ILiteQueryable<VectorRow> query, string name, string expression, int id)
        {
            AssertPlan(query.GetPlan(), name, expression, "VECTOR INDEX SEARCH");
            query.ToArray().Select(x => x.Id).Should().Equal(id);
        }

        private static void AssertPlan(BsonDocument plan, string name, string expression, string mode)
        {
            plan["index"]["name"].AsString.Should().Be(name);
            plan["index"]["expr"].AsString.Should().Be(expression);
            plan["index"]["mode"].AsString.Should().StartWith(mode);
        }

        private static void AssertCatalog(
            LiteDatabase db,
            string collection,
            params (string Name, string Expression)[] expected)
        {
            var actual = db.GetCollection("$indexes")
                .Find(Query.EQ("collection", collection))
                .ToArray();

            actual.Should().HaveCount(expected.Length);
            actual.Select(x => x["name"].AsString).Should().OnlyHaveUniqueItems();
            actual.Select(x => x["name"].AsString).Should().OnlyContain(x => !string.IsNullOrWhiteSpace(x));

            foreach (var item in expected)
            {
                var index = actual.Single(x => x["name"].AsString == item.Name);
                index["collection"].AsString.Should().Be(collection);
                index["expression"].AsString.Should().Be(item.Expression);
                index["unique"].AsBoolean.Should().Be(item.Name == "_id");
            }
        }

        private sealed class CultureScope : IDisposable
        {
            private readonly CultureInfo _previousCulture = CultureInfo.CurrentCulture;
            private readonly CultureInfo _previousUiCulture = CultureInfo.CurrentUICulture;

            public CultureScope(string name)
            {
                var culture = string.IsNullOrEmpty(name)
                    ? CultureInfo.InvariantCulture
                    : CultureInfo.GetCultureInfo(name);

                CultureInfo.CurrentCulture = culture;
                CultureInfo.CurrentUICulture = culture;
            }

            public void Dispose()
            {
                CultureInfo.CurrentCulture = _previousCulture;
                CultureInfo.CurrentUICulture = _previousUiCulture;
            }
        }
    }
}
