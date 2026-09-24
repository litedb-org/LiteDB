using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2798ExplicitMapping_Tests
    {
        public interface IRow { int Id { get; set; } string Key { get; set; } }
        public class Row : IRow
        {
            public int Id { get; set; }
            [BsonId] public string Key { get; set; }
        }
        public interface INamed { [BsonField("named_key")] string Key { get; set; } }
        public class Named : INamed { [BsonId] public string Key { get; set; } }

        [Fact]
        public void Conventional_declared_id_does_not_override_concrete_id()
        {
            var mapper = new BsonMapper();
            mapper.ToDocument(new Row { Id = 7, Key = "one" });
            mapper.GetExpression<IRow, bool>(row => row.Key == "one").Source.Should().Contain("$._id");
            mapper.GetExpression<IRow, bool>(row => row.Id == 7).Source.Should().Contain("$.Id");
            mapper.Entity<IRow>().Id(row => row.Id);
            mapper.GetExpression<IRow, bool>(row => row.Key == "one").Source.Should().Contain("$.Key");
            mapper.GetExpression<IRow, bool>(row => row.Id == 7).Source.Should().Contain("$._id");
        }

        [Fact]
        public void Explicit_attribute_fluent_and_callback_field_names_win()
        {
            var mapper = new BsonMapper();
            mapper.ToDocument(new Named { Key = "one" });
            mapper.GetExpression<INamed, bool>(row => row.Key == "one").Source.Should().Contain("$.named_key");
            mapper.Entity<INamed>().Field(row => row.Key, "fluent_key");
            mapper.GetExpression<INamed, bool>(row => row.Key == "one").Source.Should().Contain("$.fluent_key");
            var callbackMapper = new BsonMapper();
            callbackMapper.ResolveMember = (type, info, member) =>
            {
                if (type == typeof(IRow) && member.MemberName == "Key") member.FieldName = "callback_key";
            };
            callbackMapper.ToDocument(new Row { Id = 7, Key = "one" });
            callbackMapper.GetExpression<IRow, bool>(row => row.Key == "one").Source.Should().Contain("$.callback_key");
        }

#if NET8_0_OR_GREATER
        public abstract class CovariantBase { public abstract object Key { get; } }
        public class CovariantRow : CovariantBase { [BsonId] public override string Key => "one"; }

        [Fact]
        public void Covariant_abstract_id_uses_concrete_storage_mapping()
        {
            var mapper = new BsonMapper();
            mapper.ToDocument(new CovariantRow());
            mapper.GetExpression<CovariantBase, bool>(row => row.Key == (object)"one").Source.Should().Contain("$._id");
        }
#endif
    }
}
