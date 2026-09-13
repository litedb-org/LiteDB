using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Document
{
    public class ObjectId_Tests
    {
        [Fact]
        public void ObjectId_BsonValue()
        {
            var oid0 = ObjectId.Empty;
            var oid1 = ObjectId.NewObjectId();
            var oid2 = ObjectId.NewObjectId();
            var oid3 = ObjectId.NewObjectId();

            var c1 = new ObjectId(oid1);
            var c2 = new ObjectId(oid2.ToString());
            var c3 = new ObjectId(oid3.ToByteArray());

            oid0.Should().Be(ObjectId.Empty);
            oid1.Should().Be(c1);
            oid2.Should().Be(c2);
            oid3.Should().Be(c3);

            c2.CompareTo(c3).Should().Be(-1); // 1 < 2
            c1.CompareTo(c2).Should().Be(-1); // 2 < 3

            // serializations
            var joid = JsonSerializer.Serialize(c1);
            var jc1 = JsonSerializer.Deserialize(joid).AsObjectId;

            jc1.Should().Be(c1);
        }

        [Fact]
        public void ObjectId_Equals_Null_Does_Not_Throw()
        {
            var oid0 = default(ObjectId);
            var oid1 = ObjectId.NewObjectId();

            oid1.Equals(null).Should().BeFalse();
            oid1.Equals(oid0).Should().BeFalse();
        }
    }
}