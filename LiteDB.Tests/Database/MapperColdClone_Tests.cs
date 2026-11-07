using System;
using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Database
{
    public class MapperColdClone_Tests
    {
        private sealed class Row
        {
            public int Id { get; set; }
            public string SomeName { get; set; }
        }

        [Fact]
        public void Base_mapper_clone_keeps_overridden_and_builtin_serializers_and_independent_mappings()
        {
            var source = new BsonMapper();
            source.RegisterType<Uri>(value => "custom:" + value.OriginalString, value => new Uri(value.AsString.Substring(7), UriKind.Relative));
            source.Entity<Row>().Field(row => row.SomeName, "source_name");
            var first = source.Clone();
            var second = source.Clone();
            first.RegisterType<Uri>(value => "first", value => new Uri("first", UriKind.Relative));
            first.Entity<Row>().Field(row => row.SomeName, "first_name");
            var uri = new Uri("relative", UriKind.Relative);
            first.Serialize(uri).AsString.Should().Be("first");
            source.Serialize(uri).AsString.Should().Be("custom:relative");
            second.Deserialize<Uri>(second.Serialize(uri)).OriginalString.Should().Be("relative");
            second.Deserialize<TimeSpan>(second.Serialize(TimeSpan.FromMinutes(3))).Should().Be(TimeSpan.FromMinutes(3));
            var regex = new Regex("[a-z]+", RegexOptions.IgnoreCase);
            var copy = second.Deserialize<Regex>(second.Serialize(regex));
            copy.ToString().Should().Be(regex.ToString());
            copy.Options.Should().Be(regex.Options);
            second.ToDocument(new Row { Id = 1, SomeName = "kept" }).ContainsKey("source_name").Should().BeTrue();
            source.ToDocument(new Row { Id = 1, SomeName = "kept" }).ContainsKey("source_name").Should().BeTrue();
            first.ToDocument(new Row { Id = 1, SomeName = "kept" }).ContainsKey("first_name").Should().BeTrue();
        }

        [Fact]
        public void Shared_delimiter_regex_keeps_each_mappers_resolver_and_persisted_field_names_independent()
        {
            using var file = new TempFile();
            var underscore = new BsonMapper().UseLowerCaseDelimiter('_');
            var hyphen = new BsonMapper().UseLowerCaseDelimiter('-');
            var clone = underscore.Clone();
            hyphen.ToDocument(new Row { Id = 9, SomeName = "other" }).ContainsKey("some-name").Should().BeTrue();
            using (var db = new LiteDatabase(file.Filename, clone))
            {
                var rows = db.GetCollection<Row>("rows");
                rows.Insert(new Row { Id = 1, SomeName = "kept" });
                rows.EnsureIndex(row => row.SomeName);
            }
            using var reopened = new LiteDatabase(file.Filename, underscore);
            reopened.GetCollection<Row>("rows").FindOne(row => row.SomeName == "kept").Id.Should().Be(1);
            reopened.GetCollection("rows").FindById(1)["some_name"].AsString.Should().Be("kept");
        }
    }
}
