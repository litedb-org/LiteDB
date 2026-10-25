using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2798SchemaIdentity_Tests
    {
        public interface IImplicit { [BsonField] string Key { get; set; } }
        public class ImplicitRow : IImplicit { [BsonId] public string Key { get; set; } }
        public interface IExplicit { string Key { get; } }
        public class ExplicitRow : IExplicit { [BsonId] string IExplicit.Key => "one"; }
        public interface ICustom { string Key { get; set; } int Number { get; set; } }
        public class CustomRow : ICustom
        {
            public string Key { get; set; }
            [BsonId] public int Number { get; set; }
        }
        public abstract class HiddenBase { public abstract object Key { get; } }
        public class HiddenMiddle : HiddenBase { public override object Key => "base"; }
        public class HiddenRow : HiddenMiddle { [BsonId] public new string Key => "hidden"; }

        private class CustomMapper : BsonMapper
        {
            private MemberInfo GetIdMember(Type unused) => null;

            protected override MemberInfo GetIdMember(IEnumerable<MemberInfo> members)
                => base.GetIdMember(members) ?? members.FirstOrDefault(x => x.Name == "Key");
        }

        public interface IValue<out T> { T Key { get; } }
        public class StringValue : IValue<string> { [BsonId] public string Key => "one"; }
        private class NoDeclaredIdMapper : BsonMapper
        {
            protected override MemberInfo GetIdMember(IEnumerable<MemberInfo> members)
                => members.First().DeclaringType.IsInterface ? null : base.GetIdMember(members);
        }

        public class DualValue : IValue<object>, IValue<string>
        {
            [BsonId] object IValue<object>.Key => "id";
            string IValue<string>.Key => "ordinary";
        }

#if NET8_0_OR_GREATER
        private class CovariantMapper : BsonMapper
        {
            protected override PropertyInfo GetIdMember(IEnumerable<MemberInfo> members)
                => members.OfType<PropertyInfo>().First(property => property.Name == "Key");
        }

        private class HidingMapper : BsonMapper
        {
            private new MemberInfo GetIdMember(IEnumerable<MemberInfo> members) => null;
        }

        [Fact]
        public void Hidden_mapper_helper_does_not_override_virtual_id_policy()
        {
            var mapper = new HidingMapper();
            mapper.ToDocument(new ImplicitRow { Key = "one" });
            mapper.GetExpression<IImplicit, bool>(row => row.Key == "one").Source.Should().Contain("$._id");
        }

        [Fact]
        public void Covariant_mapper_override_preserves_declared_id_policy()
        {
            var mapper = new CovariantMapper();
            mapper.ToDocument(new CustomRow { Key = "one", Number = 1 });
            mapper.GetExpression<ICustom, bool>(row => row.Key == "one").Source.Should().Contain("$._id");
        }
#endif

        [Fact]
        public void Exact_generic_interface_slot_wins_over_variant_contract()
        {
            var mapper = new BsonMapper { IncludeNonPublic = true };
            mapper.ToDocument(new DualValue())["_id"].AsString.Should().Be("id");
            mapper.GetExpression<IValue<object>, bool>(row => row.Key == (object)"id").Source.Should().Contain("$._id");
        }

        [Fact]
        public void Variant_interface_query_matches_implemented_generic_slot()
        {
            var mapper = new BsonMapper();
            mapper.ToDocument(new StringValue());
            mapper.GetExpression<IValue<object>, bool>(row => row.Key == (object)"one").Source.Should().Contain("$._id");
        }

        [Fact]
        public void Custom_selection_of_no_declared_id_is_respected()
        {
            var mapper = new NoDeclaredIdMapper();
            mapper.ToDocument(new ImplicitRow { Key = "one" });
            mapper.GetExpression<IImplicit, bool>(row => row.Key == "one").Source.Should().Contain("$.Key");
        }

        [Fact]
        public void Hidden_covariant_name_does_not_impersonate_abstract_property_slot()
        {
            var mapper = new BsonMapper();
            var document = mapper.ToDocument(new HiddenRow());
            document["_id"].AsString.Should().Be("hidden");
            document["Key"].AsString.Should().Be("base");
            mapper.GetExpression<HiddenBase, bool>(row => row.Key == (object)"base").Source.Should().Contain("$.Key");
        }

        [Fact]
        public void Unnamed_field_attribute_keeps_inference_enabled()
        {
            var mapper = new BsonMapper();
            mapper.ToDocument(new ImplicitRow { Key = "one" });
            mapper.GetExpression<IImplicit, bool>(row => row.Key == "one").Source.Should().Contain("$._id");
        }

        [Fact]
        public void Explicit_interface_slot_uses_concrete_id_mapping()
        {
            var mapper = new BsonMapper { IncludeNonPublic = true };
            mapper.ToDocument(new ExplicitRow())["_id"].AsString.Should().Be("one");
            mapper.GetExpression<IExplicit, bool>(row => row.Key == "one").Source.Should().Contain("$._id");
        }

        [Fact]
        public void Custom_id_selection_on_declared_schema_takes_precedence()
        {
            var mapper = new CustomMapper();
            mapper.ToDocument(new CustomRow { Key = "one", Number = 1 });
            mapper.GetExpression<ICustom, bool>(row => row.Key == "one").Source.Should().Contain("$._id");
            mapper.GetExpression<ICustom, bool>(row => row.Number == 1).Source.Should().Contain("$.Number");
        }

        [Fact]
        public void Failed_initialization_never_publishes_successful_schema_state()
        {
            var mapper = new BsonMapper();
            EntityMapper failed = null;
            mapper.ResolveMember = (type, info, member) =>
            {
                if (type != typeof(ImplicitRow)) return;
                failed = mapper.GetEntityMapper(type);
                throw new InvalidOperationException("injected mapping failure");
            };
            Action map = () => mapper.ToDocument(new ImplicitRow { Key = "one" });
            map.Should().Throw<LiteException>();
            failed.Should().NotBeNull();
            failed.IsInitialized.Should().BeFalse();
            mapper.GetExpression<IImplicit, bool>(row => row.Key == "one").Source.Should().Contain("$.Key");
        }
    }
}
