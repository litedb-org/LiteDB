using System;
using System.Collections.Generic;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1903Dispatch_Tests
    {
        public interface IEntity { }
        public class Entity : IEntity { }
        public class Unrelated { }

        private static BsonDocument Document(Type type)
        {
            return new BsonDocument { ["_type"] = type.FullName + ", " + type.Assembly.GetName().Name };
        }

        [Fact]
        public void Resolved_decoder_returns_its_exact_result_for_interfaces_and_objects()
        {
            var mapper = new BsonMapper();
            var expected = new Entity();
            var calls = 0;
            mapper.RegisterType<Entity>(_ => BsonValue.Null, _ => { calls++; return expected; });
            var doc = Document(typeof(Entity));
            Assert.Same(expected, mapper.ToObject<IEntity>(doc));
            Assert.Same(expected, mapper.ToObject<object>(doc));
            Assert.Equal(2, calls);
        }

        [Fact]
        public void Declared_decoder_keeps_precedence()
        {
            var mapper = new BsonMapper();
            var expected = new Entity();
            mapper.RegisterType<IEntity>(_ => BsonValue.Null, _ => expected);
            mapper.RegisterType<Entity>(_ => BsonValue.Null, _ => throw new InvalidOperationException());
            Assert.Same(expected, mapper.ToObject<IEntity>(Document(typeof(Entity))));
        }

        [Fact]
        public void Unassignable_discriminator_is_rejected_before_callback()
        {
            var mapper = new BsonMapper();
            var calls = 0;
            mapper.RegisterType<Unrelated>(_ => BsonValue.Null, _ => { calls++; return new Unrelated(); });
            Assert.Throws<LiteException>(() => mapper.ToObject<IEntity>(Document(typeof(Unrelated))));
            Assert.Equal(0, calls);
        }

        [Fact]
        public void Resolved_decoder_can_return_null_or_throw_without_fallback()
        {
            var mapper = new BsonMapper();
            var doc = Document(typeof(Entity));
            mapper.RegisterType<Entity>(_ => BsonValue.Null, _ => null);
            Assert.Null(mapper.ToObject<IEntity>(doc));
            var expected = new InvalidOperationException("decoder");
            mapper.RegisterType<Entity>(_ => BsonValue.Null, _ => throw expected);
            Assert.Same(expected, Assert.Throws<InvalidOperationException>(() => mapper.ToObject<IEntity>(doc)));
        }

        public class Node : IEntity
        {
            public string Name { get; set; }
            public bool PostProcessed { get; set; }
            public List<IEntity> Children { get; set; }
        }

        private static BsonMapper MapperWithDelegatingNodeDecoder(Action onCall)
        {
            var mapper = new BsonMapper();

            mapper.RegisterType<Node>(_ => BsonValue.Null, bson =>
            {
                onCall();
                var node = (Node)mapper.Deserialize(typeof(IEntity), bson);
                node.PostProcessed = true;
                return node;
            });

            return mapper;
        }

        [Fact]
        public void Decoder_delegating_to_default_materialisation_runs_once_per_document()
        {
            var calls = 0;
            var mapper = MapperWithDelegatingNodeDecoder(() => calls++);
            var doc = Document(typeof(Node));
            doc["Name"] = "root";

            var node = Assert.IsType<Node>(mapper.ToObject<IEntity>(doc));

            Assert.Equal("root", node.Name);
            Assert.True(node.PostProcessed);
            Assert.Equal(1, calls);
        }

        [Fact]
        public void Delegating_decoder_still_decodes_nested_documents_of_the_same_type()
        {
            var calls = 0;
            var mapper = MapperWithDelegatingNodeDecoder(() => calls++);
            var child = Document(typeof(Node));
            child["Name"] = "child";
            var doc = Document(typeof(Node));
            doc["Children"] = new BsonArray { child };

            var node = Assert.IsType<Node>(mapper.ToObject<IEntity>(doc));

            var nested = Assert.IsType<Node>(Assert.Single(node.Children));
            Assert.Equal("child", nested.Name);
            Assert.True(nested.PostProcessed);
            Assert.Equal(2, calls);
        }

        [Fact]
        public void Delegating_decoder_is_used_again_after_a_failed_callback()
        {
            var calls = 0;
            var mapper = MapperWithDelegatingNodeDecoder(() => { if (calls++ == 0) throw new InvalidOperationException(); });
            var doc = Document(typeof(Node));

            Assert.Throws<InvalidOperationException>(() => mapper.ToObject<IEntity>(doc));

            Assert.True(Assert.IsType<Node>(mapper.ToObject<IEntity>(doc)).PostProcessed);
        }
    }
}
