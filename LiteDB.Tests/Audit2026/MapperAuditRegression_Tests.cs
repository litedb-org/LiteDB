using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Audit2026
{
    public class MapperAuditRegression_Tests
    {
        [Flags]
        private enum Permissions
        {
            None = 0,
            Read = 1,
            Write = 2
        }

        private sealed class SearchDocument
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public List<int> Tags { get; set; }
            public Dictionary<string, int> Values { get; set; }
            public Permissions Permissions { get; set; }
            public int Mask { get; set; }
        }

        [Fact]
        [Trait("Category", "AuditBehavior")]
        public void H15_string_contains_treats_percent_and_underscore_as_literals()
        {
            using var db = new LiteDatabase(new MemoryStream());
            var col = db.GetCollection<SearchDocument>();
            col.Insert(new SearchDocument { Id = 1, Name = "50% complete" });
            col.Insert(new SearchDocument { Id = 2, Name = "5012 complete" });
            col.Insert(new SearchDocument { Id = 3, Name = "A_B" });
            col.Insert(new SearchDocument { Id = 4, Name = "AXB" });

            col.Find(x => x.Name.Contains("50%")).Select(x => x.Id).Should().Equal(1);
            col.Find(x => x.Name.Contains("A_B")).Select(x => x.Id).Should().Equal(3);
        }

        [Fact]
        [Trait("Category", "AuditBehavior")]
        public void H49_any_operand_is_parenthesized_before_outer_comparison()
        {
            using var db = new LiteDatabase(new MemoryStream());
            var col = db.GetCollection<SearchDocument>();
            col.Insert(new SearchDocument { Id = 1, Tags = new List<int> { 1 } });
            col.Insert(new SearchDocument { Id = 2, Tags = new List<int> { 2 } });

            col.Find(x => x.Tags.Contains(1) == true).Select(x => x.Id).Should().Equal(1);
        }

        [Fact]
        [Trait("Category", "AuditBehavior")]
        public void H50_dictionary_key_is_parameterized_not_injected_into_expression_text()
        {
            const string key = "x'] OR true OR $['x";
            using var db = new LiteDatabase(new MemoryStream());
            var col = db.GetCollection<SearchDocument>();
            col.Insert(new SearchDocument
            {
                Id = 1,
                Values = new Dictionary<string, int> { [key] = 7 }
            });
            col.Insert(new SearchDocument
            {
                Id = 2,
                Values = new Dictionary<string, int> { ["safe"] = 7 }
            });

            col.Find(x => x.Values[key] == 7).Select(x => x.Id).Should().Equal(1);
        }

        [Fact]
        [Trait("Category", "AuditBehavior")]
        public void H52_flags_enum_combination_is_not_translated_as_null()
        {
            using var db = new LiteDatabase(new MemoryStream());
            var col = db.GetCollection<SearchDocument>();
            col.Insert(new SearchDocument
            {
                Id = 1,
                Permissions = Permissions.Read | Permissions.Write
            });

            col.Find(x => x.Permissions == (Permissions.Read | Permissions.Write))
                .Select(x => x.Id).Should().Equal(1);
        }

        [Fact]
        [Trait("Category", "AuditBehavior")]
        public void M137_bitwise_integer_operators_preserve_bitwise_semantics()
        {
            using var db = new LiteDatabase(new MemoryStream());
            var col = db.GetCollection<SearchDocument>();
            col.Insert(new SearchDocument { Id = 1, Mask = 3 });
            col.Insert(new SearchDocument { Id = 2, Mask = 2 });

            col.Find(x => (x.Mask & 1) == 1).Select(x => x.Id).Should().Equal(1);
        }

        [Fact]
        [Trait("Category", "AuditBehavior")]
        public void M135_constructor_matching_is_invariant_under_turkish_culture()
        {
            var original = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
                var document = new BsonDocument { ["Id"] = 42 };

                BsonMapper.Global.Deserialize<TurkishImmutable>(document).Id.Should().Be(42);
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        }

        private sealed class TurkishImmutable
        {
            public int Id { get; }

            public TurkishImmutable(int id)
            {
                this.Id = id;
            }
        }
    }
}
