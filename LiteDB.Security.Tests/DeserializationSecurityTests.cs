using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using Xunit;

namespace LiteDB.Security.Tests
{
    public class DeserializationSecurityTests
    {
        public class Envelope
        {
            public object Value { get; set; }
            public Dictionary<string, object> Map { get; set; }
            public object[] Items { get; set; }
        }

        public static IEnumerable<object[]> UnsafeShapes()
        {
            foreach (var type in new[] { typeof(Process), typeof(ProcessStartInfo), typeof(ArrayList) })
                foreach (var shape in new[] { "root", "member", "dictionary", "array" })
                    yield return new object[] { type, shape };
        }

        [Theory]
        [MemberData(nameof(UnsafeShapes))]
        public void Known_unsafe_types_are_rejected_before_the_instantiator(Type unsafeType, string shape)
        {
            var attempted = false;
            var mapper = new BsonMapper(type =>
            {
                if (type == unsafeType)
                {
                    attempted = true;
                    throw new InvalidOperationException("The unsafe object must never be constructed.");
                }
                return Activator.CreateInstance(type);
            });
            var payload = new BsonDocument { ["_type"] = unsafeType.AssemblyQualifiedName };
            Action deserialize;
            switch (shape)
            {
                case "root": deserialize = () => mapper.ToObject(typeof(object), payload); break;
                case "member": deserialize = () => mapper.ToObject<Envelope>(new BsonDocument { ["Value"] = payload }); break;
                case "dictionary": deserialize = () => mapper.ToObject<Envelope>(new BsonDocument
                    { ["Map"] = new BsonDocument { ["entry"] = payload } }); break;
                default: deserialize = () => mapper.ToObject<Envelope>(new BsonDocument
                    { ["Items"] = new BsonArray { payload } }); break;
            }
            var failure = Assert.Throws<LiteException>(deserialize);
            Assert.Equal(unsafeType == typeof(Process) ? LiteException.AVOID_USE_OF_PROCESS : LiteException.ILLEGAL_DESERIALIZATION_TYPE,
                failure.ErrorCode);
            Assert.Contains(unsafeType.FullName, failure.Message);
            Assert.False(attempted);
        }

        [Fact]
        public void Assignability_is_checked_before_custom_instantiation()
        {
            var attempted = false;
            var mapper = new BsonMapper(type => { attempted = true; throw new InvalidOperationException(); });
            var failure = Assert.Throws<LiteException>(() => mapper.ToObject<Envelope>(
                new BsonDocument { ["_type"] = typeof(Dictionary<string, string>).AssemblyQualifiedName }));
            Assert.Equal(LiteException.DATA_TYPE_NOT_ASSIGNABLE, failure.ErrorCode);
            Assert.False(attempted);
        }

        [Fact]
        public void Unknown_type_names_report_the_existing_error()
        {
            var failure = Assert.Throws<LiteException>(() => new BsonMapper().ToObject<object>(
                new BsonDocument { ["_type"] = "Unknown.Type, Missing.Assembly" }));
            Assert.Equal(LiteException.INVALID_TYPED_NAME, failure.ErrorCode);
        }
    }
}
