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
    }

    public enum Color
    {
        None = 0,
        Red = 1
    }

    private static ILiteCollection<Account> CreateCollection(LiteDatabase db)
    {
        var collection = db.GetCollection<Account>("accounts");

        collection.Insert(new Account { Id = 1, Balance = 120.5m, Flags = 0, Mask = 0, Unsigned = 0, Ratio = 0.1 + 0.2, Slot = 7, Tint = Color.None });
        collection.Insert(new Account { Id = 2, Balance = -120.5m, Flags = -1, Mask = -1, Unsigned = 1, Ratio = -1.5, Slot = 0, Tint = Color.Red });
        collection.Insert(new Account { Id = 3, Balance = 0m, Flags = 7, Mask = 7, Unsigned = 7, Ratio = 1e30, Slot = 255, Tint = Color.Red });
        collection.Insert(new Account { Id = 4, Balance = -3m, Flags = int.MinValue, Mask = long.MinValue, Unsigned = uint.MaxValue, Ratio = 0, Slot = 1, Tint = Color.None });

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
        BsonMapper.Global.GetExpression<Account, decimal>(x => -x.Balance).Source.Should().Be("(0-$.Balance)");
        BsonMapper.Global.GetExpression<Account, int>(x => ~x.Flags).Source.Should().Be("(-1-$.Flags)");
        BsonMapper.Global.GetExpression<Account, decimal>(x => +x.Balance).Source.Should().Be("$.Balance");
    }

    [Fact]
    public void Complement_of_an_unsupported_type_is_rejected()
    {
        Action unsigned = () => BsonMapper.Global.GetExpression<Account, uint>(x => ~x.Unsigned);
        Action enumeration = () => BsonMapper.Global.GetExpression<Account, Color>(x => ~x.Tint);

        unsigned.Should().Throw<NotSupportedException>();
        enumeration.Should().Throw<NotSupportedException>();
    }
}
