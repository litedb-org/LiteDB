using System;
using System.Reflection;
using System.Reflection.Emit;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2894ConstructorCode_Tests
    {
        // Trimmed assemblies lose constructor parameter names; an emitted constructor without DefineParameter has none either.
        private static Type CreateTypeWithUnnamedBsonCtor()
        {
            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Issue2894Unnamed"), AssemblyBuilderAccess.Run);
            var type = assembly.DefineDynamicModule("main").DefineType("Unnamed", TypeAttributes.Public | TypeAttributes.Class);
            var ctor = type.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, new[] { typeof(int) });

            ctor.SetCustomAttribute(new CustomAttributeBuilder(typeof(BsonCtorAttribute).GetConstructor(Type.EmptyTypes), new object[0]));

            var il = ctor.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes));
            il.Emit(OpCodes.Ret);

            return type.CreateTypeInfo().AsType();
        }

        [Fact]
        public void Missing_constructor_parameter_names_keep_the_invalid_ctor_code_inside_the_mapping_context()
        {
            var type = CreateTypeWithUnnamedBsonCtor();
            var mapper = new BsonMapper();

            Action map = () => mapper.Deserialize(type, new BsonDocument { ["_id"] = 1 });

            var error = map.Should().Throw<LiteException>().Which;
            error.ErrorCode.Should().Be(LiteException.INVALID_CTOR);
            error.Message.Should().Contain(type.FullName, "the entity context added by #2894 must stay");
        }
    }
}
