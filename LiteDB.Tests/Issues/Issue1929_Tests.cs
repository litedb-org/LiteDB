#if !NETFRAMEWORK
using System;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1929_Tests
    {
        [Fact]
        public void FindById_Should_Deserialize_System_Index_Property()
        {
            using var db = new LiteDatabase(new MemoryStream());
            var collection = db.GetCollection<Person>("people");
            var person = new Person
            {
                Name = "Alex",
                Selection = System.Index.FromEnd(2)
            };

            collection.Insert(person);

            var result = collection.FindById(person.Id);

            Assert.NotNull(result);
            Assert.Equal(person.Name, result.Name);
            Assert.Equal(person.Selection, result.Selection);
        }

        [Fact]
        public void Deserialize_Should_Handle_System_Index_With_Object_Type_Discriminator()
        {
            var mapper = new BsonMapper();
            var expected = System.Index.FromEnd(3);

            var value = mapper.Serialize(typeof(object), expected);
            var result = mapper.Deserialize(typeof(object), value);

            var index = Assert.IsType<System.Index>(result);
            Assert.Equal(expected, index);
        }

        [Fact]
        public void Deserialize_Should_Handle_System_Index_With_Lower_Case_Delimiter()
        {
            var mapper = new BsonMapper().UseLowerCaseDelimiter();
            var expected = System.Index.FromEnd(4);

            var value = mapper.Serialize(expected);
            var result = mapper.Deserialize(typeof(System.Index), value);

            var index = Assert.IsType<System.Index>(result);
            Assert.Equal(expected, index);
        }

        [Fact]
        public void Deserialize_Should_Honor_A_Custom_Constructor_For_System_Index()
        {
            var mapper = new BsonMapper();
            mapper.Entity<System.Index>().Ctor(document => new System.Index(
                document["offset"].AsInt32,
                document["fromEnd"].AsBoolean));
            var stored = new BsonDocument
            {
                ["offset"] = 17,
                ["fromEnd"] = true
            };

            var result = mapper.Deserialize<System.Index>(stored);

            Assert.Equal(System.Index.FromEnd(17), result);
        }

        [Fact]
        public void A_User_Type_Merely_Named_System_Index_Should_Map_Normally()
        {
            var mapper = new BsonMapper();
            var userType = CreateUnrelatedSystemIndexType();
            var stored = new BsonDocument
            {
                ["Value"] = 12,
                ["IsFromEnd"] = true
            };

            // This is not the BCL's System.Index. It only has the same full
            // name, which must not activate LiteDB's special-case converter.
            var result = mapper.Deserialize(userType, stored);

            Assert.Equal(userType, result.GetType());
            Assert.Equal(12, userType.GetProperty("Value").GetValue(result));
            Assert.Equal(true, userType.GetProperty("IsFromEnd").GetValue(result));
        }

        private static Type CreateUnrelatedSystemIndexType()
        {
            var assembly = AssemblyBuilder.DefineDynamicAssembly(
                new AssemblyName("Issue1929UserTypes_" + Guid.NewGuid().ToString("N")),
                AssemblyBuilderAccess.Run);
            var module = assembly.DefineDynamicModule("UserTypes");
            var type = module.DefineType(
                "System.Index",
                TypeAttributes.Public | TypeAttributes.Class);

            type.DefineDefaultConstructor(MethodAttributes.Public);
            DefineAutoProperty(type, "Value", typeof(int));
            DefineAutoProperty(type, "IsFromEnd", typeof(bool));

            return type.CreateTypeInfo().AsType();
        }

        private static void DefineAutoProperty(TypeBuilder type, string name, Type propertyType)
        {
            var field = type.DefineField("_" + name, propertyType, FieldAttributes.Private);
            var property = type.DefineProperty(name, PropertyAttributes.None, propertyType, null);
            var getter = type.DefineMethod(
                "get_" + name,
                MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
                propertyType,
                Type.EmptyTypes);
            var getterIl = getter.GetILGenerator();
            getterIl.Emit(OpCodes.Ldarg_0);
            getterIl.Emit(OpCodes.Ldfld, field);
            getterIl.Emit(OpCodes.Ret);
            var setter = type.DefineMethod(
                "set_" + name,
                MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
                typeof(void),
                new[] { propertyType });
            var setterIl = setter.GetILGenerator();
            setterIl.Emit(OpCodes.Ldarg_0);
            setterIl.Emit(OpCodes.Ldarg_1);
            setterIl.Emit(OpCodes.Stfld, field);
            setterIl.Emit(OpCodes.Ret);

            property.SetGetMethod(getter);
            property.SetSetMethod(setter);
        }

        public class Person
        {
            public string Id { get; set; } = Guid.NewGuid().ToString();

            public string Name { get; set; }

            public System.Index Selection { get; set; }
        }
    }
}
#endif
