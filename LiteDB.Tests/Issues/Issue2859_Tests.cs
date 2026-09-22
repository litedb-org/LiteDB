using System.Linq;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2859_Tests
    {
        private static readonly Collation IgnoreCase = new Collation("en-US/IgnoreCase");

        [Fact]
        public void Document_comparison_propagates_collation_to_non_first_values()
        {
            using (new AssertionScope())
            {
                AssertEqualUnderIgnoreCase(
                    DocumentWithLeaf("Ledger"),
                    DocumentWithLeaf("lEDGER"),
                    "Ledger",
                    "lEDGER");

                AssertOrderingFollowsLeafOracle(
                    DocumentWithLeaf("Zebra"),
                    DocumentWithLeaf("apple"),
                    "Zebra",
                    "apple");
            }
        }

        [Fact]
        public void Array_comparison_propagates_collation_to_non_first_elements()
        {
            using (new AssertionScope())
            {
                AssertEqualUnderIgnoreCase(
                    ArrayWithLeaf("Ledger"),
                    ArrayWithLeaf("lEDGER"),
                    "Ledger",
                    "lEDGER");

                AssertOrderingFollowsLeafOracle(
                    ArrayWithLeaf("Zebra"),
                    ArrayWithLeaf("apple"),
                    "Zebra",
                    "apple");
            }
        }

        [Fact]
        public void Comparison_propagates_collation_through_nested_document_array_mixtures()
        {
            using (new AssertionScope())
            {
                AssertEqualUnderIgnoreCase(
                    DocumentArrayDocument("Ledger"),
                    DocumentArrayDocument("lEDGER"),
                    "Ledger",
                    "lEDGER");

                AssertOrderingFollowsLeafOracle(
                    DocumentArrayDocument("Zebra"),
                    DocumentArrayDocument("apple"),
                    "Zebra",
                    "apple");

                AssertEqualUnderIgnoreCase(
                    ArrayDocumentArray("Receipt"),
                    ArrayDocumentArray("rECEIPT"),
                    "Receipt",
                    "rECEIPT");

                AssertOrderingFollowsLeafOracle(
                    ArrayDocumentArray("Zebra"),
                    ArrayDocumentArray("apple"),
                    "Zebra",
                    "apple");
            }
        }

        [Fact]
        public void Configured_database_predicates_compare_nested_values_with_its_collation()
        {
            using var db = new LiteDatabase(new ConnectionString
            {
                Filename = ":memory:",
                Collation = IgnoreCase
            });
            var collection = db.GetCollection("entries");

            var wrongDocumentGuard = DocumentArrayDocument("Ledger");
            wrongDocumentGuard["outerGuard"] = 999;
            var wrongArrayGuard = ArrayDocumentArray("Receipt");
            wrongArrayGuard[1].AsDocument["innerGuard"] = 999;

            collection.Insert(new[]
            {
                Row(1, DocumentArrayDocument("Ledger")),
                Row(2, ArrayDocumentArray("Receipt")),
                Row(3, DocumentArrayDocument("different")),
                Row(4, ArrayDocumentArray("different")),
                Row(5, wrongDocumentGuard),
                Row(6, wrongArrayGuard)
            });

            using (new AssertionScope())
            {
                FindIds(collection, DocumentArrayDocument("Ledger")).Should().Equal(new[] { 1 },
                    "an exact-case query is the healthy query-path control");
                FindIds(collection, DocumentArrayDocument("lEDGER")).Should().Equal(new[] { 1 },
                    "database collation must reach a document-array-document leaf");
                FindIds(collection, ArrayDocumentArray("Receipt")).Should().Equal(new[] { 2 },
                    "an exact-case query is the healthy query-path control");
                FindIds(collection, ArrayDocumentArray("rECEIPT")).Should().Equal(new[] { 2 },
                    "database collation must reach an array-document-array leaf");
            }
        }

        private static void AssertEqualUnderIgnoreCase(
            BsonValue left,
            BsonValue right,
            string leftLeaf,
            string rightLeaf)
        {
            var ignoreCaseOracle = IgnoreCase.Compare(leftLeaf, rightLeaf);
            var binaryOracle = Collation.Binary.Compare(leftLeaf, rightLeaf);

            using (new AssertionScope())
            {
                ignoreCaseOracle.Should().Be(0, "the independent string-leaf oracle defines equality");
                binaryOracle.Should().NotBe(0, "the ordinal control must distinguish this fixture");
                AssertMatchesLeafOracles(left, right, ignoreCaseOracle, binaryOracle);
                IgnoreCase.Equals(left, right).Should().BeTrue();
                Collation.Binary.Equals(left, right).Should().BeFalse();
            }
        }

        private static void AssertOrderingFollowsLeafOracle(
            BsonValue left,
            BsonValue right,
            string leftLeaf,
            string rightLeaf)
        {
            var ignoreCaseOracle = IgnoreCase.Compare(leftLeaf, rightLeaf);
            var binaryOracle = Collation.Binary.Compare(leftLeaf, rightLeaf);

            using (new AssertionScope())
            {
                ignoreCaseOracle.Should().BePositive("case-insensitive Zebra sorts after apple");
                binaryOracle.Should().BeNegative("ordinal Zebra sorts before apple");
                AssertMatchesLeafOracles(left, right, ignoreCaseOracle, binaryOracle);
            }
        }

        private static void AssertMatchesLeafOracles(
            BsonValue left,
            BsonValue right,
            int ignoreCaseOracle,
            int binaryOracle)
        {
            IgnoreCase.Compare(left, right).Should().Be(ignoreCaseOracle,
                "Collation.Compare must propagate its collation to every nested value");
            left.CompareTo(right, IgnoreCase).Should().Be(ignoreCaseOracle,
                "BsonValue.CompareTo must propagate its supplied collation");
            IgnoreCase.Compare(right, left).Should().Be(-ignoreCaseOracle,
                "comparison must remain antisymmetric");

            Collation.Binary.Compare(left, right).Should().Be(binaryOracle,
                "binary comparison must retain ordinal semantics");
            left.CompareTo(right).Should().Be(binaryOracle,
                "the parameterless overload must retain its documented binary semantics");
            Collation.Binary.Compare(right, left).Should().Be(-binaryOracle,
                "the binary healthy control must remain antisymmetric");
        }

        private static BsonDocument DocumentWithLeaf(string leaf)
        {
            return new BsonDocument
            {
                ["guard"] = 101,
                ["leaf"] = leaf
            };
        }

        private static BsonArray ArrayWithLeaf(string leaf)
        {
            return new BsonArray
            {
                101,
                leaf
            };
        }

        private static BsonDocument DocumentArrayDocument(string leaf)
        {
            return new BsonDocument
            {
                ["outerGuard"] = 101,
                ["payload"] = new BsonArray
                {
                    202,
                    new BsonDocument
                    {
                        ["innerGuard"] = 303,
                        ["leaf"] = leaf
                    }
                }
            };
        }

        private static BsonArray ArrayDocumentArray(string leaf)
        {
            return new BsonArray
            {
                101,
                new BsonDocument
                {
                    ["innerGuard"] = 202,
                    ["payload"] = new BsonArray
                    {
                        303,
                        leaf
                    }
                }
            };
        }

        private static BsonDocument Row(int id, BsonValue value)
        {
            return new BsonDocument
            {
                ["_id"] = id,
                ["value"] = value
            };
        }

        private static int[] FindIds(ILiteCollection<BsonDocument> collection, BsonValue value)
        {
            return collection
                .Find(BsonExpression.Create("value = @0", value))
                .Select(x => x["_id"].AsInt32)
                .OrderBy(x => x)
                .ToArray();
        }
    }
}
