using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Mapper
{
    public class DataAnnotationsMapping_Tests
    {
        [Fact]
        public void DataAnnotations_are_opt_in_and_mapper_scoped()
        {
            var entity = new AnnotatedEntity
            {
                Id = 1,
                ExternalId = 10,
                Secret = "hidden"
            };
            var mapper = new BsonMapper().UseDataAnnotations();

            var document = mapper.ToDocument(entity);
            document["_id"].Should().Be(10);
            document["Id"].Should().Be(1);
            document.ContainsKey("Secret").Should().BeFalse();

            var separateMapper = new BsonMapper();
            var separateDocument = separateMapper.ToDocument(entity);
            separateDocument["_id"].Should().Be(1);
            separateDocument.ContainsKey("Secret").Should().BeTrue();
        }

        [Fact]
        public void DataAnnotations_are_inherited_by_overridden_members()
        {
            var mapper = new BsonMapper().UseDataAnnotations();
            var entity = new DerivedAnnotatedEntity
            {
                ExternalId = 20,
                Name = "value",
                Secret = "hidden"
            };

            var document = mapper.ToDocument(entity);

            document["_id"].Should().Be(20);
            document["Name"].Should().Be("value");
            document.ContainsKey("Secret").Should().BeFalse();
        }

        [Fact]
        public void BsonId_takes_precedence_over_inherited_Key()
        {
            var mapper = new BsonMapper().UseDataAnnotations();
            var entity = new BsonIdPrecedenceEntity
            {
                ExternalId = 20,
                LiteDbId = 30,
                Secret = "hidden"
            };

            var document = mapper.ToDocument(entity);

            document["_id"].Should().Be(30);
            document["ExternalId"].Should().Be(20);
            document.ContainsKey("Secret").Should().BeFalse();
            mapper.GetEntityMapper(typeof(BsonIdPrecedenceEntity)).Id.AutoId.Should().BeFalse();
        }

        [Fact]
        public void BsonId_auto_id_setting_takes_precedence_over_Key_on_the_same_member()
        {
            var mapper = new BsonMapper().UseDataAnnotations();

            var entityMapper = mapper.GetEntityMapper(typeof(CombinedIdEntity));

            entityMapper.Id.MemberName.Should().Be(nameof(CombinedIdEntity.Key));
            entityMapper.Id.AutoId.Should().BeTrue();
        }

        public class AnnotatedEntity
        {
            public int Id { get; set; }

            [Key]
            public int ExternalId { get; set; }

            [NotMapped]
            public string Secret { get; set; }
        }

        public abstract class BaseAnnotatedEntity
        {
            [Key]
            public virtual int ExternalId { get; set; }

            [NotMapped]
            public virtual string Secret { get; set; }
        }

        public class DerivedAnnotatedEntity : BaseAnnotatedEntity
        {
            public override int ExternalId { get; set; }

            public override string Secret { get; set; }

            public string Name { get; set; }
        }

        public class BsonIdPrecedenceEntity : BaseAnnotatedEntity
        {
            [BsonId(false)]
            public int LiteDbId { get; set; }
        }

        public class CombinedIdEntity
        {
            [Key]
            [BsonId]
            public int Key { get; set; }
        }
    }
}
