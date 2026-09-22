using System;
using System.Linq;
using System.Linq.Expressions;

using FluentAssertions;

using Xunit;

namespace LiteDB.Tests.Issues;

public class Issue2921_Tests
{
    public class Account
    {
        public int Id { get; set; }
        public decimal Balance { get; set; }
        public int Flags { get; set; }
        public long Mask { get; set; }
        public uint Unsigned { get; set; }
        public double Ratio { get; set; }
        public byte Slot { get; set; }
        public Color Tint { get; set; }
        public long? NullableMask { get; set; }
        public double? NullableRatio { get; set; }
    }

    public enum Color
    {
        None = 0,
        Red = 1
    }

    private static ILiteCollection<Account> CreateCollection(LiteDatabase db)
    {
        var collection = db.GetCollection<Account>("accounts");

        collection.Insert(new Account { Id = 1, Balance = 120.5m, Flags = 0, Mask = 0, Unsigned = 0, Ratio = 0.1 + 0.2, Slot = 7, Tint = Color.None, NullableMask = 0, NullableRatio = 0.1 + 0.2 });
        collection.Insert(new Account { Id = 2, Balance = -120.5m, Flags = -1, Mask = -1, Unsigned = 1, Ratio = -1.5, Slot = 0, Tint = Color.Red, NullableMask = long.MinValue, NullableRatio = -1e30 });
        collection.Insert(new Account { Id = 3, Balance = 0m, Flags = 7, Mask = 7, Unsigned = 7, Ratio = 1e30, Slot = 255, Tint = Color.Red, NullableMask = null, NullableRatio = null });
        collection.Insert(new Account { Id = 4, Balance = -3m, Flags = int.MinValue, Mask = long.MinValue, Unsigned = uint.MaxValue, Ratio = 0, Slot = 1, Tint = Color.None, NullableMask = long.MaxValue, NullableRatio = 1e30 });

        return collection;
    }

    public static TheoryData<string, Expression<Func<Account, bool>>> UnaryPredicates => new()
    {
        { "negated decimal member", x => -x.Balance > 100m },
        { "negated decimal member, inclusive", x => -x.Balance >= 0m },
        { "negated integer member", x => -x.Flags < 0 },
        { "negated integer member, positive", x => -x.Flags > 0 },
        { "negated sub expression", x => -(x.Flags + 1) < 0 },
        { "negated key", x => -x.Id == -2 },
        { "unary plus", x => +x.Balance > 100m },
        { "complement of an integer", x => ~x.Flags == 0 },
        { "complement of an integer, non zero", x => ~x.Flags == -8 },
        { "complement of a long", x => ~x.Mask == 0L },
        { "negated constant", x => x.Balance == -(-120.5m) },
        { "negated double member", x => -x.Ratio == -(0.1 + 0.2) },
        { "negated double member, large", x => -x.Ratio < -1e29 },
        { "negated long member", x => -x.Mask > 0 },
        { "negated long member at the boundary", x => -x.Mask == long.MinValue },
        { "complement of a byte member", x => ~x.Slot == -8 },
        { "complement through an explicit widening cast", x => ~(long)x.Flags == 0L },
        { "complement through an explicit widening cast, boundary", x => ~(long)x.Flags == int.MaxValue },
        { "complement of an unsigned member widened explicitly", x => ~(long)x.Unsigned == -2L },
        { "negated nullable long member", x => -x.NullableMask > 0 },
        { "negated nullable long member at the boundary", x => -x.NullableMask == long.MinValue },
        { "negated nullable double member, equality", x => -x.NullableRatio == -(0.1 + 0.2) },
        { "negated nullable double member, large", x => -x.NullableRatio == -1e30 },
        { "complement through a round trip cast", x => ~(int)(long)x.Flags == 0 },
        { "complement through a round trip cast, boundary", x => ~(int)(long)x.Flags == int.MaxValue },
    };

    [Theory]
    [MemberData(nameof(UnaryPredicates))]
    public void Unary_operators_match_linq_to_objects(string because, Expression<Func<Account, bool>> predicate)
    {
        using var db = new LiteDatabase(":memory:");
        var collection = CreateCollection(db);

        var inMemory = collection.FindAll().Where(predicate.Compile()).Select(x => x.Id).OrderBy(x => x).ToArray();
        var inDatabase = collection.Find(predicate).Select(x => x.Id).OrderBy(x => x).ToArray();

        inDatabase.Should().Equal(inMemory, because);
    }

    [Fact]
    public void Negation_reaches_the_translated_expression()
    {
        BsonMapper.Global.GetExpression<Account, decimal>(x => -x.Balance).Source.Should().Be("(@p0-$.Balance)");
        BsonMapper.Global.GetExpression<Account, int>(x => ~x.Flags).Source.Should().Be("(@p0-$.Flags)");
        BsonMapper.Global.GetExpression<Account, decimal>(x => +x.Balance).Source.Should().Be("$.Balance");
    }

    [Fact]
    public void Widening_a_negated_member_does_not_reuse_the_narrow_expression()
    {
        using var db = new LiteDatabase(":memory:");
        var collection = CreateCollection(db);

        collection.Find(x => -x.Flags > 0).Select(x => x.Id).ToArray();

        var inMemory = collection.FindAll().Where(x => -(long)x.Flags > 0).Select(x => x.Id).OrderBy(x => x).ToArray();
        var inDatabase = collection.Find(x => -(long)x.Flags > 0).Select(x => x.Id).OrderBy(x => x).ToArray();

        inDatabase.Should().Equal(inMemory);
    }

    [Fact]
    public void Complement_of_an_unsigned_result_is_rejected()
    {
        var slot = Expression.Parameter(typeof(Account), "x");
        var asByte = Expression.Convert(Expression.Property(slot, nameof(Account.Slot)), typeof(byte));
        var complement = Expression.Lambda<Func<Account, byte>>(Expression.Not(asByte), slot);

        Action unsignedResult = () => BsonMapper.Global.GetExpression(complement);

        unsignedResult.Should().Throw<NotSupportedException>().WithMessage("*~Byte*");
    }

    [Fact]
    public void Negation_of_a_non_numeric_member_is_rejected()
    {
        Action enumeration = () => BsonMapper.Global.GetExpression<Account, int>(x => -(int)x.Tint);

        enumeration.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Complement_of_an_unsupported_type_is_rejected()
    {
        Action unsigned = () => BsonMapper.Global.GetExpression<Account, uint>(x => ~x.Unsigned);
        Action enumeration = () => BsonMapper.Global.GetExpression<Account, Color>(x => ~x.Tint);

        unsigned.Should().Throw<NotSupportedException>().WithMessage("*~UInt32*");
        enumeration.Should().Throw<NotSupportedException>().WithMessage("*~Color*");
    }

    [Fact]
    public void Complement_through_a_truncating_cast_chain_is_rejected()
    {
        Action truncating = () => BsonMapper.Global.GetExpression<Account, long>(x => ~(long)(int)x.Mask);
        Action narrowed = () => BsonMapper.Global.GetExpression<Account, int>(x => ~(int)(short)x.Flags);

        truncating.Should().Throw<NotSupportedException>();
        narrowed.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Complement_through_a_narrowing_cast_is_rejected()
    {
        Action narrowing = () => BsonMapper.Global.GetExpression<Account, int>(x => ~(int)x.Mask);
        Action unsignedToSigned = () => BsonMapper.Global.GetExpression<Account, int>(x => ~(int)x.Unsigned);

        narrowing.Should().Throw<NotSupportedException>();
        unsignedToSigned.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Checked_negation_is_rejected()
    {
        Action negateChecked = () => BsonMapper.Global.GetExpression<Account, int>(x => checked(-x.Flags));

        negateChecked.Should().Throw<NotSupportedException>();
    }
}
