using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Mapper
{
    public class CustomAttributeRegistration_Tests
    {
        [Fact]
        public void Attribute_types_can_be_registered_per_mapper()
        {
            var mapper = new BsonMapper();
            mapper.RegisterIdAttribute(typeof(CustomIdAttribute));
            mapper.RegisterIgnoreAttribute(typeof(CustomIgnoreAttribute));

            var document = mapper.ToDocument(new CustomEntity
            {
                Key = 10,
                Name = "value",
                Secret = "hidden"
            });

            document["_id"].Should().Be(10);
            document["Name"].Should().Be("value");
            document.ContainsKey("Secret").Should().BeFalse();
        }

        [Fact]
        public void Attribute_names_are_mapper_scoped()
        {
            var mapper = new BsonMapper();
            mapper.RegisterIdAttributeByName(typeof(NamedIdAttribute).FullName);
            mapper.RegisterIgnoreAttributeByName(typeof(NamedIgnoreAttribute).FullName);
            var entity = new NamedAttributeEntity { Id = 1, Key = 20, Secret = "hidden" };

            var document = mapper.ToDocument(entity);
            document["_id"].Should().Be(20);
            document["Id"].Should().Be(1);
            document.ContainsKey("Secret").Should().BeFalse();

            var separateMapper = new BsonMapper();
            var separateDocument = separateMapper.ToDocument(entity);
            separateDocument["_id"].Should().Be(1);
            separateDocument.ContainsKey("Secret").Should().BeTrue();
        }

        [Fact]
        public void Registered_attributes_are_inherited_by_overridden_members()
        {
            var mapper = new MappingProbe();
            mapper.RegisterIdAttribute(typeof(CustomIdAttribute));
            mapper.RegisterIgnoreAttribute(typeof(CustomIgnoreAttribute));

            var document = mapper.ToDocument(new DerivedCustomEntity
            {
                Key = 30,
                Secret = "hidden",
                Name = "value"
            });

            document["_id"].Should().Be(30);
            document["Name"].Should().Be("value");
            document.ContainsKey("Secret").Should().BeFalse();
        }

        [Fact]
        public async Task Registration_and_mapping_are_safe_under_concurrency()
        {
            var mapper = new MappingProbe();

            foreach (var index in Enumerable.Range(0, 100))
            {
                mapper.RegisterIdAttributeByName("Tests.UnusedId" + index);
                mapper.RegisterIgnoreAttributeByName("Tests.UnusedIgnore" + index);
            }

            mapper.RegisterIdAttributeByName(typeof(NamedIdAttribute).FullName);
            mapper.RegisterIgnoreAttributeByName(typeof(NamedIgnoreAttribute).FullName);

            using var start = new ManualResetEventSlim();
            var writer = Task.Run(() =>
            {
                start.Wait();

                foreach (var index in Enumerable.Range(100, 400))
                {
                    mapper.RegisterIdAttributeByName("Tests.UnusedId" + index);
                    mapper.RegisterIgnoreAttributeByName("Tests.UnusedIgnore" + index);
                }
            });

            var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            {
                start.Wait();

                for (var index = 0; index < 250; index++)
                {
                    var entityMapper = mapper.BuildNamedAttributeMapping();

                    if (entityMapper.Id.MemberName != nameof(NamedAttributeEntity.Key) ||
                        entityMapper.Members.Any(x => x.MemberName == nameof(NamedAttributeEntity.Secret)))
                    {
                        throw new InvalidOperationException("Concurrent attribute mapping produced an invalid document.");
                    }
                }
            })).ToArray();

            start.Set();
            await Task.WhenAll(readers.Concat(new[] { writer }));
        }

        [Fact]
        public void Registration_rejects_invalid_attribute_types_and_names()
        {
            var mapper = new BsonMapper();

            Action nullIdType = () => mapper.RegisterIdAttribute(null);
            Action invalidIgnoreType = () => mapper.RegisterIgnoreAttribute(typeof(string));
            Action emptyIdName = () => mapper.RegisterIdAttributeByName(" ");
            Action nullIgnoreName = () => mapper.RegisterIgnoreAttributeByName(null);

            nullIdType.Should().Throw<ArgumentNullException>().WithParameterName("attributeType");
            invalidIgnoreType.Should().Throw<ArgumentException>().WithParameterName("attributeType");
            emptyIdName.Should().Throw<ArgumentException>().WithParameterName("attributeFullName");
            nullIgnoreName.Should().Throw<ArgumentException>().WithParameterName("attributeFullName");
        }

        [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = true)]
        public sealed class CustomIdAttribute : Attribute
        {
        }

        [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = true)]
        public sealed class CustomIgnoreAttribute : Attribute
        {
        }

        [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = true)]
        public sealed class NamedIdAttribute : Attribute
        {
        }

        [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = true)]
        public sealed class NamedIgnoreAttribute : Attribute
        {
        }

        public class CustomEntity
        {
            [CustomId]
            public int Key { get; set; }

            public string Name { get; set; }

            [CustomIgnore]
            public string Secret { get; set; }
        }

        public class NamedAttributeEntity
        {
            public int Id { get; set; }

            [NamedId]
            public int Key { get; set; }

            [NamedIgnore]
            public string Secret { get; set; }
        }

        public abstract class BaseCustomEntity
        {
            [CustomId]
            public virtual int Key { get; set; }

            [CustomIgnore]
            public virtual string Secret { get; set; }
        }

        public class DerivedCustomEntity : BaseCustomEntity
        {
            public override int Key { get; set; }

            public override string Secret { get; set; }

            public string Name { get; set; }
        }

        private sealed class MappingProbe : BsonMapper
        {
            public EntityMapper BuildNamedAttributeMapping()
            {
                var mapper = new EntityMapper(typeof(NamedAttributeEntity));
                this.BuildEntityMapper(mapper);
                return mapper;
            }
        }
    }
}
