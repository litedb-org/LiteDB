using System;
using System.Reflection;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1192Metadata_Tests
    {
        public class Envelope
        {
            public Action Callback { get; set; }
            public MemberInfo Member { get; set; }
            public int Value { get; set; }
        }

        private sealed class MetadataMapper : BsonMapper
        {
            protected override BsonDocument SerializeObject(Type type, object value, int depth)
            {
                if (value is MemberInfo member) return new BsonDocument { ["name"] = member.Name };
                if (value is Delegate) return new BsonDocument { ["kind"] = "delegate" };
                return base.SerializeObject(type, value, depth);
            }
        }

        [Fact]
        public void Virtual_object_serializer_can_supply_a_metadata_representation()
        {
            var mapper = new MetadataMapper();
            var member = typeof(string).GetProperty(nameof(string.Length));
            Assert.Equal(nameof(string.Length), mapper.Serialize<MemberInfo>(member).AsDocument["name"].AsString);
            Action action = () => throw new InvalidOperationException("Must not invoke");
            Assert.Equal("delegate", mapper.Serialize(action).AsDocument["kind"].AsString);
        }

        [Fact]
        public void Runtime_custom_serializer_handles_an_object_typed_delegate()
        {
            var mapper = new BsonMapper();
            mapper.RegisterType<Action>(value => "callback", value => null);
            object callback = (Action)(() => throw new InvalidOperationException("Must not run"));
            Assert.Equal("callback", mapper.Serialize(callback).AsString);
        }

        [Fact]
        public void Unsupported_metadata_serializes_as_null_without_invoking_a_delegate()
        {
            var mapper = new BsonMapper();
            var document = mapper.Serialize(new Envelope
            {
                Callback = () => throw new InvalidOperationException("Must not run"),
                Member = typeof(Envelope).GetProperty(nameof(Envelope.Value)),
                Value = 42
            }).AsDocument;
            Assert.True(document["Callback"].IsNull);
            Assert.True(document["Member"].IsNull);
            Assert.Equal(42, document["Value"].AsInt32);
        }

        [Fact]
        public void Explicit_custom_serializers_can_represent_delegate_and_member_metadata()
        {
            var mapper = new BsonMapper();
            mapper.RegisterType<Action>(value => "callback", value => null);
            mapper.RegisterType<MemberInfo>(value => value.Name, value => null);
            var document = mapper.Serialize(new Envelope
            {
                Callback = () => throw new InvalidOperationException("Must not run"),
                Member = typeof(Envelope).GetProperty(nameof(Envelope.Value)),
                Value = 42
            }).AsDocument;
            Assert.Equal("callback", document["Callback"].AsString);
            Assert.Equal("Value", document["Member"].AsString);
            Assert.Equal(42, document["Value"].AsInt32);
        }
    }
}
