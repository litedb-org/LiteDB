using System;
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
    }
}
