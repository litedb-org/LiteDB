using System;
using System.Collections.Generic;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2860_Tests
    {
        public static IEnumerable<object[]> UriCases()
        {
            yield return new object[] { "img/a.png", false };
            yield return new object[] { "../img/a.png?size=2#preview", false };
            yield return new object[] { "/a/b", false };
            yield return new object[] { "https://example.test/img/a.png?size=2#preview", true };
            yield return new object[] { "urn:example:animal:ferret:nose", true };
            yield return new object[] { "file:///a/b", true };
        }

        public static IEnumerable<object[]> AmbiguousDepthTypes()
        {
            yield return new object[]
            {
                new DepthEnvelope<FirstDomain.Node> { Child = new FirstDomain.Node() },
                typeof(FirstDomain.Node)
            };
            yield return new object[]
            {
                new DepthEnvelope<SecondDomain.Node> { Child = new SecondDomain.Node() },
                typeof(SecondDomain.Node)
            };
        }

        [Theory]
        [MemberData(nameof(UriCases))]
        public void Mapper_reads_independently_authored_Uri_strings_with_their_kind(string text, bool isAbsolute)
        {
            var mapper = new BsonMapper();
            var raw = new BsonDocument
            {
                ["_id"] = 1,
                ["Address"] = text
            };

            var loaded = mapper.ToObject<UriRecord>(raw);

            AssertUri(loaded.Address, text, isAbsolute);
        }

        [Theory]
        [MemberData(nameof(UriCases))]
        public void Database_round_trip_preserves_Uri_text_and_kind(string text, bool isAbsolute)
        {
            var source = new Uri(text, isAbsolute ? UriKind.Absolute : UriKind.Relative);
            var mapper = new BsonMapper();
            using var db = new LiteDatabase(":memory:", mapper);
            var typed = db.GetCollection<UriRecord>("uris");

            typed.Insert(new UriRecord { Id = 1, Address = source });

            var raw = db.GetCollection("uris").FindById(1);
            raw["Address"].IsString.Should().BeTrue();
            raw["Address"].AsString.Should().Be(source.ToString());

            var loaded = typed.FindById(1);
            AssertUri(loaded.Address, source.ToString(), isAbsolute);
        }

        [Theory]
        [MemberData(nameof(AmbiguousDepthTypes))]
        public void Max_depth_error_disambiguates_types_with_the_same_simple_name(object value, Type nestedType)
        {
            var mapper = new BsonMapper { MaxDepth = 1 };

            Action serialize = () => mapper.ToDocument(value.GetType(), value);

            var exception = serialize.Should().Throw<LiteException>().Which;
            exception.ErrorCode.Should().Be(LiteException.DOCUMENT_MAX_DEPTH);
            exception.Message.Should().Be(
                $"Document has more than 1 nested documents in '{nestedType.FullName}'. " +
                "Check for circular references (use DbRef).");
        }

        private static void AssertUri(Uri actual, string text, bool isAbsolute)
        {
            var expected = new Uri(text, isAbsolute ? UriKind.Absolute : UriKind.Relative);

            actual.Should().NotBeNull();
            actual.IsAbsoluteUri.Should().Be(isAbsolute);
            actual.OriginalString.Should().Be(text);
            actual.ToString().Should().Be(expected.ToString());
        }

        public class UriRecord
        {
            [BsonId]
            public int Id { get; set; }

            public Uri Address { get; set; }
        }

        public class DepthEnvelope<T>
        {
            public T Child { get; set; }
        }

        public static class FirstDomain
        {
            public class Node
            {
            }
        }

        public static class SecondDomain
        {
            public class Node
            {
            }
        }
    }
}
