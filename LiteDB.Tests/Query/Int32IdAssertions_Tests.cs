using System;
using System.Collections.Generic;
using FluentAssertions;
using Xunit;
using Xunit.Sdk;

namespace LiteDB.Tests.QueryTest
{
    public class Int32IdAssertions_Tests
    {
        [Fact]
        public void ReorderingAndMatchingMultiplicity_AreAccepted()
        {
            Int32IdAssertions.BeEquivalentTo(new BsonValue[] { 3, 1, 2 }, new BsonValue[] { 1, 2, 3 });
            Int32IdAssertions.BeEquivalentTo(new BsonValue[] { 2, 1, 1 }, new BsonValue[] { 1, 2, 1 });
            Int32IdAssertions.BeEquivalentTo(Array.Empty<BsonValue>(), Array.Empty<BsonValue>());
        }

        [Theory]
        [InlineData("missing")]
        [InlineData("extra")]
        [InlineData("duplicate-and-drop")]
        [InlineData("wrong-member")]
        public void MembershipAndMultiplicityFailures_AgreeWithOriginalAssertion(string damage)
        {
            var expected = new BsonValue[] { 1, 2, 3 };
            var actual = damage == "missing" ? new BsonValue[] { 1, 2 } :
                damage == "extra" ? new BsonValue[] { 1, 2, 3, 4 } :
                damage == "duplicate-and-drop" ? new BsonValue[] { 1, 1, 3 } : new BsonValue[] { 1, 2, 4 };
            Action original = () => actual.Should().BeEquivalentTo(expected);
            Action candidate = () => Int32IdAssertions.BeEquivalentTo(actual, expected);
            original.Should().Throw<XunitException>();
            candidate.Should().Throw<XunitException>();
        }

        [Fact]
        public void SameLengthAndDistinctMembers_DoNotHideUnequalMultiplicity()
        {
            var actual = new BsonValue[] { 1, 2, 2 };
            var expected = new BsonValue[] { 1, 1, 2 };
            Action original = () => actual.Should().BeEquivalentTo(expected);
            Action candidate = () => Int32IdAssertions.BeEquivalentTo(actual, expected);
            original.Should().Throw<XunitException>();
            candidate.Should().Throw<XunitException>();
        }

        [Theory]
        [MemberData(nameof(CoercibleWrongTypes))]
        public void ConvertibleWrongTypes_CannotHideBehindScalarProjection(BsonValue invalid)
        {
            var expected = new BsonValue[] { 1 };
            var actual = new[] { invalid };
            invalid.AsInt32.Should().Be(1, "this control must defeat a bare AsInt32 projection");
            Action candidate = () => Int32IdAssertions.BeEquivalentTo(actual, expected);
            candidate.Should().Throw<XunitException>().WithMessage("*actual IDs must retain*Int32*");
        }

        public static IEnumerable<object[]> CoercibleWrongTypes()
        {
            yield return new object[] { new BsonValue("1") };
            yield return new object[] { new BsonValue(1.4) };
            yield return new object[] { new BsonValue(1.4m) };
            yield return new object[] { new BsonValue(1L) };
            yield return new object[] { new BsonValue(true) };
        }

        [Fact]
        public void FixtureTypeContract_IsStricterThanNumericBsonEquality()
        {
            var actual = new BsonValue[] { 1L };
            var expected = new BsonValue[] { 1 };
            actual.Should().BeEquivalentTo(expected); // Numeric BSON equality accepts Int64 here.
            Action candidate = () => Int32IdAssertions.BeEquivalentTo(actual, expected);
            candidate.Should().Throw<XunitException>().WithMessage("*actual IDs must retain*Int32*");
        }

        [Fact]
        public void WrongExpectedType_IsRejectedBeforeConversion()
        {
            Action candidate = () => Int32IdAssertions.BeEquivalentTo(new BsonValue[] { 1 }, new BsonValue[] { "1" });
            candidate.Should().Throw<XunitException>().WithMessage("*expected IDs must retain*Int32*");
        }

        [Fact]
        public void MissingOrNullId_IsRejected()
        {
            Action missing = () => Int32IdAssertions.BeEquivalentTo(new[] { new BsonDocument()["_id"] }, new BsonValue[] { 0 });
            Action nullReference = () => Int32IdAssertions.BeEquivalentTo(new BsonValue[] { null }, new BsonValue[] { 0 });
            missing.Should().Throw<XunitException>().WithMessage("*actual IDs must retain*Int32*");
            nullReference.Should().Throw<XunitException>().WithMessage("*actual IDs must retain*Int32*");
        }
    }
}
