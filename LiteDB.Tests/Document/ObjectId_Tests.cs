using System;
using FluentAssertions;
using LiteDB;
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

        [Fact]
        public void ObjectId_ToString_Minimizes_Allocations()
        {
            var objectId = ObjectId.NewObjectId();

            objectId.ToString();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var before = GC.GetAllocatedBytesForCurrentThread();
            var hex = objectId.ToString();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            hex.Should().HaveLength(24);
            allocated.Should().BeLessThan(220);
        }

        [Fact]
        public void ObjectId_FromHex_Minimizes_Allocations()
        {
            var original = ObjectId.NewObjectId();
            var hex = original.ToString();

            _ = new ObjectId(hex);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var before = GC.GetAllocatedBytesForCurrentThread();
            var parsed = new ObjectId(hex);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            parsed.Should().Be(original);
            allocated.Should().BeLessThan(220);
        }
    }
}
