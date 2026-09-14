using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2801_Tests
    {
        public static IEnumerable<object[]> ArraySources
        {
            get
            {
                yield return new object[]
                {
                    "string array",
                    new string[] { "head", null, "tail" }
                };
                yield return new object[]
                {
                    "fixed BsonValue array",
                    new BsonValue[] { "head", null, "tail" }
                };
                yield return new object[]
                {
                    "mutable IList<BsonValue>",
                    new List<BsonValue> { "head", null, "tail" }
                };
                yield return new object[]
                {
                    "read-only IList<BsonValue>",
                    new ReadOnlyCollection<BsonValue>(
                        new List<BsonValue> { "head", null, "tail" })
                };
            }
        }

        public static IEnumerable<object[]> DocumentSources
        {
            get
            {
                yield return new object[]
                {
                    "IDictionary<string, BsonValue>",
                    new Dictionary<string, BsonValue>
                    {
                        ["Name"] = "first",
                        ["Nothing"] = null
                    }
                };
                yield return new object[]
                {
                    "read-only IDictionary<string, BsonValue>",
                    new ReadOnlyDictionary<string, BsonValue>(
                        new Dictionary<string, BsonValue>
                        {
                            ["Name"] = "first",
                            ["Nothing"] = null
                        })
                };
                yield return new object[]
                {
                    "IDictionary<string, object>",
                    new Dictionary<string, object>
                    {
                        ["Name"] = "first",
                        ["Nothing"] = null
                    }
                };
            }
        }

        [Theory]
        [MemberData(nameof(ArraySources))]
        public void Object_constructor_array_inputs_are_complete_mutable_values_or_are_clearly_rejected(
            string description,
            object source)
        {
            var value = ConstructOrAssertClearRejection(source, "BsonArray", description);
            if (ReferenceEquals(value, null)) return;

            value.Type.Should().Be(BsonType.Array);
            value.IsArray.Should().BeTrue();

            var array = value.AsArray;
            ((object)array).Should().NotBeNull();
            Assert.Same(array, value.AsArray);
            array.Count.Should().Be(3);
            array[0].AsString.Should().Be("head");
            ((object)array[1]).Should().NotBeNull("CLR nulls must become BSON nulls");
            array[1].IsNull.Should().BeTrue();
            array[2].AsString.Should().Be("tail");
            value[0].AsString.Should().Be("head", "the value reports that it is an array");

            value[0] = "changed";
            array.Add(37);
            array.Add(null);

            Assert.Same(array, value.AsArray);
            value.AsArray.Count.Should().Be(5);
            value.AsArray[0].AsString.Should().Be("changed");
            value.AsArray[3].AsInt32.Should().Be(37);
            ((object)value.AsArray[4]).Should().NotBeNull();
            value.AsArray[4].IsNull.Should().BeTrue();
            value.ToString().Should().Be("[\"changed\",null,\"tail\",37,null]");
        }

        [Theory]
        [MemberData(nameof(DocumentSources))]
        public void Object_constructor_dictionary_inputs_use_mutable_BsonDocument_semantics_or_are_clearly_rejected(
            string description,
            object source)
        {
            var value = ConstructOrAssertClearRejection(source, "BsonDocument", description);
            if (ReferenceEquals(value, null)) return;

            value.Type.Should().Be(BsonType.Document);
            value.IsDocument.Should().BeTrue();

            var document = value.AsDocument;
            ((object)document).Should().NotBeNull();
            Assert.Same(document, value.AsDocument);
            document.Count.Should().Be(2);
            document["name"].AsString.Should().Be("first", "BSON document keys ignore case");
            ((object)document["NOTHING"]).Should().NotBeNull("CLR nulls must become BSON nulls");
            document["NOTHING"].IsNull.Should().BeTrue();
            value["NAME"].AsString.Should().Be("first", "the value reports that it is a document");

            value["nAmE"] = "changed";
            document["Added"] = 29;

            Assert.Same(document, value.AsDocument);
            value.AsDocument.Count.Should().Be(3, "changing only key casing must not add a field");
            value.AsDocument["NAME"].AsString.Should().Be("changed");
            value.AsDocument["added"].AsInt32.Should().Be(29);
            JsonSerializer.Deserialize(value.ToString()).AsDocument["nothing"].IsNull.Should().BeTrue();
        }

        [Fact]
        public void Wrapped_array_equality_and_hash_are_consistent_after_independent_value_checks()
        {
            var wrapped = ConstructOrAssertClearRejection(
                new BsonValue[] { 5, "five", null }, "BsonArray", "BsonValue array");
            if (ReferenceEquals(wrapped, null)) return;

            var canonical = new BsonArray(5L, "five", BsonValue.Null);
            ((object)wrapped.AsArray).Should().NotBeNull();
            wrapped.AsArray.Count.Should().Be(3);
            wrapped.AsArray[0].AsInt32.Should().Be(5);
            wrapped.AsArray[1].AsString.Should().Be("five");
            wrapped.AsArray[2].IsNull.Should().BeTrue();

            wrapped.Equals(canonical).Should().BeTrue();
            canonical.Equals(wrapped).Should().BeTrue();
            wrapped.GetHashCode().Should().Be(canonical.GetHashCode());
            wrapped.Equals(new BsonArray(6, "five", BsonValue.Null)).Should().BeFalse();
        }

        [Fact]
        public void Wrapped_document_equality_and_hash_follow_case_insensitive_document_semantics()
        {
            var wrapped = ConstructOrAssertClearRejection(
                new Dictionary<string, BsonValue> { ["Number"] = 5, ["Text"] = "five" },
                "BsonDocument",
                "BsonValue dictionary");
            if (ReferenceEquals(wrapped, null)) return;

            var canonical = new BsonDocument { ["NUMBER"] = 5L, ["TEXT"] = "five" };
            ((object)wrapped.AsDocument).Should().NotBeNull();
            wrapped.AsDocument.Count.Should().Be(2);
            wrapped.AsDocument["number"].AsInt32.Should().Be(5);
            wrapped.AsDocument["text"].AsString.Should().Be("five");

            wrapped.Equals(canonical).Should().BeTrue();
            canonical.Equals(wrapped).Should().BeTrue();
            wrapped.GetHashCode().Should().Be(canonical.GetHashCode());
            wrapped.Equals(new BsonDocument { ["NUMBER"] = 6, ["TEXT"] = "five" }).Should().BeFalse();
        }

        [Fact]
        public void Wrapped_array_maps_and_survives_binary_database_reopen()
        {
            var wrapped = ConstructOrAssertClearRejection(
                new string[] { "alpha", null, "omega" }, "BsonArray", "string array");
            if (ReferenceEquals(wrapped, null)) return;

            ((object)wrapped.AsArray).Should().NotBeNull();
            var mapped = BsonMapper.Global.Deserialize<string[]>(wrapped);
            mapped.Length.Should().Be(3);
            mapped[0].Should().Be("alpha");
            mapped[1].Should().BeNull();
            mapped[2].Should().Be("omega");

            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename))
            {
                db.GetCollection("rows").Insert(
                    new BsonDocument { ["_id"] = 41, ["Value"] = wrapped });
            }
            using (var db = new LiteDatabase(file.Filename))
            {
                var stored = db.GetCollection("rows").FindById(41)["Value"];
                stored.IsArray.Should().BeTrue();
                stored.AsArray.Count.Should().Be(3);
                stored.AsArray[0].AsString.Should().Be("alpha");
                stored.AsArray[1].IsNull.Should().BeTrue();
                stored.AsArray[2].AsString.Should().Be("omega");
                stored.ToString().Should().Be("[\"alpha\",null,\"omega\"]");
            }
        }

        [Fact]
        public void Wrapped_document_maps_and_preserves_case_insensitive_id_and_index_on_reopen()
        {
            var wrapped = ConstructOrAssertClearRejection(
                new Dictionary<string, BsonValue>
                {
                    ["_id"] = 71,
                    ["Name"] = "Ada",
                    ["Count"] = 2
                },
                "BsonDocument",
                "BsonValue dictionary");
            if (ReferenceEquals(wrapped, null)) return;

            ((object)wrapped.AsDocument).Should().NotBeNull();
            wrapped.AsDocument["_ID"] = 72;
            wrapped.AsDocument.Count.Should().Be(3, "_id and _ID identify the same BSON field");
            var mapped = BsonMapper.Global.Deserialize<MappedPayload>(wrapped);
            mapped.Name.Should().Be("Ada");
            mapped.Count.Should().Be(2);

            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename))
            {
                var rows = db.GetCollection("rows");
                rows.EnsureIndex("Name");
                rows.Insert(wrapped.AsDocument);
            }
            using (var db = new LiteDatabase(file.Filename))
            {
                var rows = db.GetCollection("rows");
                ((object)rows.FindById(71)).Should().BeNull();
                var byId = rows.FindById(72);
                ((object)byId).Should().NotBeNull();
                byId.Count.Should().Be(3);
                byId["_id"].AsInt32.Should().Be(72);
                byId["name"].AsString.Should().Be("Ada");
                byId["count"].AsInt32.Should().Be(2);
                rows.FindOne(Query.EQ("name", "Ada"))["_id"].AsInt32.Should().Be(72);
            }
        }

        [Fact]
        public void Descriptive_collection_rejection_must_not_disable_supported_object_values()
        {
            object number = 37;
            object clrNull = null;

            var numericValue = new BsonValue(number);
            numericValue.Type.Should().Be(BsonType.Int32);
            numericValue.AsInt32.Should().Be(37);
            new BsonValue(clrNull).IsNull.Should().BeTrue();
        }

        private static BsonValue ConstructOrAssertClearRejection(
            object source,
            string replacementType,
            string description)
        {
            try
            {
                return new BsonValue(source);
            }
            catch (ArgumentException exception)
            {
                var namesReplacement = exception.Message.IndexOf(
                    replacementType, StringComparison.OrdinalIgnoreCase) >= 0;
                var namesMapper = exception.Message.IndexOf(
                    "BsonMapper", StringComparison.OrdinalIgnoreCase) >= 0;

                (namesReplacement || namesMapper).Should().BeTrue(
                    $"a rejected {description} must direct callers to {replacementType} or BsonMapper");
                return null;
            }
        }

        public class MappedPayload
        {
            public string Name { get; set; }
            public int Count { get; set; }
        }
    }
}
