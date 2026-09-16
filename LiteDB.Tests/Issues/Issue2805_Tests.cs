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

        public class LegacyRow
        {
            public int Id { get; set; }
            public string Größe { get; set; }
            public string TITLE_I { get; set; }
        }

        [Fact]
        public void Auto_named_ensure_index_reuses_an_index_persisted_under_the_legacy_ascii_name()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection<LegacyRow>("rows");
            rows.Insert(new LegacyRow { Id = 1, Größe = "hit" });
            // `Gre` is what the old ASCII-only filter generated for `$.Größe`.
            rows.EnsureIndex("Gre", "$.Größe").Should().BeTrue();

            rows.EnsureIndex("$.Größe").Should().BeFalse();
            rows.EnsureIndex(x => x.Größe).Should().BeFalse();

            AssertCatalog(db, "rows", ("_id", "$._id"), ("Gre", "$.Größe"));
            AssertPlan(rows.Query().Where(x => x.Größe == "hit").GetPlan(), "Gre", "$.Größe", "INDEX SEEK");
        }

        [Fact]
        public void Auto_named_ensure_index_reuses_names_generated_by_the_old_turkish_culture_filter()
        {
            using var cultureScope = new CultureScope("tr-TR");
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection<LegacyRow>("rows");
            rows.EnsureIndex("D", "$.ID").Should().BeTrue();
            rows.EnsureIndex("TTLE", x => x.TITLE_I).Should().BeTrue();

            rows.EnsureIndex("$.ID").Should().BeFalse();
            rows.EnsureIndex(x => x.TITLE_I).Should().BeFalse();

            AssertCatalog(db, "rows", ("_id", "$._id"), ("D", "$.ID"), ("TTLE", "$.TITLE_I"));
        }

        [Fact]
        public void Auto_named_ensure_index_does_not_create_a_missing_collection_or_touch_other_collections()
        {
            using var db = new LiteDatabase(":memory:");
            db.GetCollection<LegacyRow>("other").EnsureIndex("Gre", "$.Größe").Should().BeTrue();

            db.GetCollection<LegacyRow>("rows").EnsureIndex(x => x.Größe).Should().BeTrue();

            AssertCatalog(db, "other", ("_id", "$._id"), ("Gre", "$.Größe"));
            AssertCatalog(db, "rows", ("_id", "$._id"), ("Größe", "$.Größe"));
        }

        [Fact]
        public void Auto_named_ensure_index_answers_from_a_read_only_database_when_the_index_exists()
        {
            using var file = new TempFile();
            using (var setup = new LiteDatabase(file.Filename))
            {
                setup.GetCollection<LegacyRow>("rows").EnsureIndex("Gre", "$.Größe").Should().BeTrue();
            }

            using var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, ReadOnly = true });
            var rows = db.GetCollection<LegacyRow>("rows");

            rows.EnsureIndex(x => x.Größe).Should().BeFalse();
            Assert.Throws<NotSupportedException>(() => rows.EnsureIndex(x => x.TITLE_I));
        }

        [Fact]
        public void Auto_named_vector_index_reuses_the_legacy_name_and_still_validates_its_options()
        {
            using var db = new LiteDatabase(":memory:");
            ILiteCollection<VectorRow> vectors = db.GetCollection<VectorRow>("vectors");
            var target = new float[] { 1, 0 };
            var options = new VectorIndexOptions(2);
            vectors.Insert(new VectorRow { Id = 1, 向量 = target });
            // The old ASCII-only filter dropped every Unicode letter, so such an index carries another name.
            vectors.EnsureIndex("legacy", x => x.向量, options).Should().BeTrue();

            vectors.EnsureIndex(x => x.向量, options).Should().BeFalse();
            vectors.EnsureIndex(BsonExpression.Create("$.向量"), options).Should().BeFalse();
            Assert.Throws<LiteException>(() => vectors.EnsureIndex(x => x.向量, new VectorIndexOptions(3)));

            AssertCatalog(db, "vectors", ("_id", "$._id"), ("legacy", "$.向量"));
            AssertVectorQuery(vectors.Query().WhereNear(x => x.向量, target, 0.001), "legacy", "$.向量", 1);
        }

        [Fact]
        public void Persisted_legacy_names_are_reused_until_dropped_and_then_replaced_by_canonical_names()
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
                rows.EnsureIndex(x => x.IName).Should().BeFalse();
                vectors.EnsureIndex(x => x.IEmbedding, options).Should().BeFalse();
                using (new CultureScope("tr-TR"))
                {
                    rows.EnsureIndex(x => x.IName).Should().BeFalse();
                    vectors.EnsureIndex(x => x.IEmbedding, options).Should().BeFalse();
                }
                AssertCatalog(db, "rows", ("_id", "$._id"), ("Name", "$.IName"));
                AssertCatalog(db, "vectors", ("_id", "$._id"), ("Embedding", "$.IEmbedding"));
                rows.Find(x => x.IName == "hit").Select(x => x.Id).Should().Equal(1);
                vectors.Query().WhereNear(x => x.IEmbedding, target, 0.001)
                    .ToArray().Select(x => x.Id).Should().Equal(1);
                rows.DropIndex("Name").Should().BeTrue();
                vectors.DropIndex("Embedding").Should().BeTrue();
                rows.EnsureIndex(x => x.IName).Should().BeTrue();
                vectors.EnsureIndex(x => x.IEmbedding, options).Should().BeTrue();
                AssertCatalog(db, "rows", ("_id", "$._id"), ("IName", "$.IName"));
                AssertCatalog(db, "vectors", ("_id", "$._id"), ("IEmbedding", "$.IEmbedding"));
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
