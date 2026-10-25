using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2578_Tests
    {
        public abstract class Entity
        {
            public abstract Guid Id { get; }
            public abstract string Name { get; }
        }
        public class Playlist : Entity
        {
            public Playlist(string name) { PlaylistId = Guid.NewGuid(); Name = name; }
            [BsonCtor]
            public Playlist(Guid _id, string name) { PlaylistId = _id; Name = name; UsedBsonCtor = true; }
            public Guid PlaylistId { get; }
            public override Guid Id => PlaylistId;
            public override string Name { get; }
            public string Comment { get; set; }
            public List<Guid> ImageIds { get; set; } = new List<Guid>();
            [BsonIgnore]
            public bool UsedBsonCtor { get; }
        }

        public class StoredNames
        {
            public int Id { get; }
            [BsonField("stored_name")]
            public string Name { get; }

            [BsonCtor]
            public StoredNames(int _id, string stored_name)
            {
                Id = _id;
                Name = stored_name;
            }
        }

        public class SelectedOverload
        {
            public object Value { get; }
            [BsonIgnore]
            public bool UsedBsonCtor { get; }

            [BsonCtor]
            public SelectedOverload(object value)
            {
                Value = value;
                UsedBsonCtor = true;
            }

            public SelectedOverload(string value)
            {
                Value = value;
            }
        }

        [Fact]
        public void Stored_field_names_bind_and_selected_constructor_is_invoked_exactly()
        {
            var mapper = new BsonMapper();
            var named = mapper.ToObject<StoredNames>(new BsonDocument { ["_id"] = 37, ["stored_name"] = "raw" });
            named.Id.Should().Be(37);
            named.Name.Should().Be("raw");
            var selected = mapper.ToObject<SelectedOverload>(new BsonDocument { ["Value"] = "raw" });
            selected.UsedBsonCtor.Should().BeTrue("runtime argument types must not select a different overload");
            selected.Value.Should().Be("raw");
        }

        [Fact]
        public void BsonCtor_receives_stored_id_for_overridden_get_only_properties()
        {
            var original = new Playlist("original") { Comment = "retained" };
            original.ImageIds.Add(Guid.Parse("0920cfcc-051b-47c5-a975-b7a74bc1d16d"));
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename, new BsonMapper()))
            {
                db.GetCollection<Playlist>("lists").Insert(original);
            }
            using (var db = new LiteDatabase(file.Filename, new BsonMapper()))
            {
                db.GetCollection("lists").FindById(original.Id)["_id"].AsGuid.Should().Be(original.Id);
                var col = db.GetCollection<Playlist>("lists");
                var loaded = col.FindAll().Single();
                loaded.UsedBsonCtor.Should().BeTrue();
                loaded.Id.Should().Be(original.Id);
                loaded.Name.Should().Be("original");
                loaded.Comment.Should().Be("retained");
                loaded.ImageIds.Should().Equal(original.ImageIds);
                col.FindById(loaded.Id).Should().NotBeNull();
            }
        }
    }
}
