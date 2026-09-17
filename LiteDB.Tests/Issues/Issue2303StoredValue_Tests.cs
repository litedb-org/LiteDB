using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2303StoredValue_Tests
    {
        public class IgnoringPerson
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public IgnoringPerson(int id, string name) { Id = id; }
        }

        public class TransformingPerson
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public TransformingPerson(int id, string name) { Id = id; Name = "ctor:" + name; }
        }

        public class NormalisingPerson
        {
            private string _name;
            public int Id { get; set; }
            public string Name { get => _name; set => _name = value?.Trim().ToUpper(); }
            public NormalisingPerson(int id, string name) { Id = id; }
        }

        public class Tagged
        {
            private List<string> _tags;
            public Tagged(List<string> tags) { _tags = tags; }
            public List<string> Tags { get => _tags; set { _tags = value; Sets++; } }
            [BsonIgnore] public int Sets { get; private set; }
        }

        public class CopyingTagged
        {
            public CopyingTagged(List<string> tags) { Tags = new List<string> { "ctor" }; }
            public List<string> Tags { get; set; }
        }

        public record Account(int Id, string Name)
        {
            public string Note { get; init; }
        }

        public class Group
        {
            public int Id { get; set; }
            public List<IgnoringPerson> Members { get; set; }
            public List<Issue2303_Tests.Point> Points { get; set; }
        }

        [Fact]
        public void Member_ignored_by_its_constructor_is_populated_from_the_stored_value()
        {
            var person = new BsonMapper().ToObject<IgnoringPerson>(new BsonDocument { ["_id"] = 1, ["Name"] = "alice" });
            person.Id.Should().Be(1);
            person.Name.Should().Be("alice");
        }

        [Fact]
        public void Stored_value_wins_over_a_constructor_that_transforms_its_argument()
        {
            var person = new BsonMapper().ToObject<TransformingPerson>(new BsonDocument { ["_id"] = 1, ["Name"] = "alice" });
            person.Name.Should().Be("alice");
        }

        [Fact]
        public void Normalising_setter_still_runs_for_a_constructor_bound_member()
        {
            var person = new BsonMapper().ToObject<NormalisingPerson>(new BsonDocument { ["_id"] = 1, ["Name"] = " alice " });
            person.Name.Should().Be("ALICE");
        }

        [Fact]
        public void Collection_stored_by_the_constructor_is_not_set_again()
        {
            var tagged = new BsonMapper().ToObject<Tagged>(new BsonDocument { ["Tags"] = new BsonArray { "a", "b" } });
            tagged.Tags.Should().Equal("a", "b");
            tagged.Sets.Should().Be(0);
        }

        [Fact]
        public void Collection_replaced_by_the_constructor_is_populated_from_the_stored_value()
        {
            var tagged = new BsonMapper().ToObject<CopyingTagged>(new BsonDocument { ["Tags"] = new BsonArray { "a", "b" } });
            tagged.Tags.Should().Equal("a", "b");
        }

        [Fact]
        public void Record_round_trips_positional_and_init_only_members()
        {
            var mapper = new BsonMapper();
            var account = new Account(7, "alice") { Note = "vip" };
            mapper.ToObject<Account>(mapper.ToDocument(account)).Should().Be(account);
        }

        [Fact]
        public void Constructor_types_nested_in_lists_keep_stored_values_and_skip_redundant_setters()
        {
            var document = new BsonDocument
            {
                ["_id"] = 1,
                ["Members"] = new BsonArray
                {
                    new BsonDocument { ["_id"] = 1, ["Name"] = "alice" },
                    new BsonDocument { ["_id"] = 2, ["Name"] = "bob" }
                },
                ["Points"] = new BsonArray { new BsonDocument { ["X"] = 1.5, ["Y"] = 2.5, ["Label"] = "p" } }
            };
            var group = new BsonMapper().ToObject<Group>(document);
            group.Members.Select(x => x.Name).Should().Equal("alice", "bob");
            group.Points.Single().CoordinateSets.Should().Be(0);
            group.Points.Single().LabelSets.Should().Be(1);
        }
            public class GuardedGetter
        {
            private string _name;

            public GuardedGetter(int id, string name)
            {
                Id = id;
            }

            public int Id { get; set; }

            public string Name
            {
                get => _name ?? throw new InvalidOperationException("not initialised");
                set => _name = value;
            }
        }

        [Fact]
        public void A_getter_that_throws_on_the_fresh_instance_falls_back_to_the_setter()
        {
            var mapper = new BsonMapper();

            var result = mapper.ToObject<GuardedGetter>(new BsonDocument { ["_id"] = 1, ["Name"] = "alice" });

            Assert.Equal("alice", result.Name);
        }
    }
}
