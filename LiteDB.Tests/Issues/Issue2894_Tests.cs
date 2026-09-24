using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2894_Tests
    {
        public class EnumerableEntity : IEnumerable<string>
        {
            public int Id { get; set; }
            public string Name { get; set; }

            public IEnumerator<string> GetEnumerator()
            {
                yield return this.Name;
            }

            IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();
        }

        public class ErrorLog
        {
            public int Id { get; set; }
            public Exception Error { get; set; }
        }

        public class StreamHolder
        {
            public int Id { get; set; }
            public Stream Data { get; set; }
        }

        [Fact]
        public void Enumerable_root_entity_is_rejected_with_its_type_name_without_inserting()
        {
            using var db = new LiteDatabase(new MemoryStream());
            Action insert = () => db.GetCollection<EnumerableEntity>("bags")
                .Insert(new EnumerableEntity { Id = 1, Name = "bag" });

            var error = insert.Should().Throw<LiteException>().Which;
            error.ErrorCode.Should().Be(LiteException.MAPPING_ERROR);
            error.Message.Should().Contain(nameof(EnumerableEntity)).And.Contain("root document");
            db.GetCollection("bags").Count().Should().Be(0);
        }

        [Fact]
        public void Supported_exception_member_keeps_its_message_without_traversing_runtime_metadata()
        {
            Exception captured;
            try
            {
                throw new InvalidOperationException("boom");
            }
            catch (Exception error)
            {
                captured = error;
            }

            using var db = new LiteDatabase(new MemoryStream());
            db.GetCollection<ErrorLog>().Insert(new ErrorLog { Id = 1, Error = captured });
            var stored = db.GetCollection("ErrorLog").FindById(1);
            stored["Error"]["Message"].AsString.Should().Be("boom");
            stored["Error"]["TargetSite"].IsNull.Should().BeTrue();
        }

        [Fact]
        public void Stream_member_failure_names_the_entity_and_member()
        {
            using var db = new LiteDatabase(new MemoryStream());
            Action insert = () => db.GetCollection<StreamHolder>()
                .Insert(new StreamHolder { Id = 1, Data = new MemoryStream() });

            AssertContextualFailure(insert, nameof(StreamHolder), nameof(StreamHolder.Data));
        }

        public class Broken
        {
            public int Value => throw new InvalidOperationException("getter {failed}");
        }
        public class Parent
        {
            public Broken Child { get; set; }
        }

        [Fact]
        public void Nested_getter_failure_preserves_member_chain_and_original_exception()
        {
            Action serialize = () => new BsonMapper().ToDocument(new Parent { Child = new Broken() });
            var error = serialize.Should().Throw<LiteException>().Which;
            error.Message.Should().Contain("Parent.Child").And.Contain("Broken.Value").And.Contain("getter {failed}");
            error.GetBaseException().Should().BeOfType<InvalidOperationException>();
        }

        [Fact]
        public void Mapping_initialization_failure_preserves_original_exception_and_literal_braces()
        {
            var original = new InvalidOperationException("mapping {failed}");
            var mapper = new BsonMapper
            {
                ResolveMember = (type, info, member) => throw original
            };
            Action serialize = () => mapper.ToDocument(new Parent());
            var error = serialize.Should().Throw<LiteException>().Which;
            error.InnerException.Should().BeSameAs(original);
            error.Message.Should().Contain(nameof(Parent)).And.Contain(original.Message);
            error.ErrorCode.Should().Be(LiteException.MAPPING_ERROR);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void Missing_constructor_parameter_names_report_metadata_and_trimming_guidance(bool hasDefault, bool attributed)
        {
            var assembly = AssemblyBuilder.DefineDynamicAssembly(
                new AssemblyName("MissingParameterNames_" + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.Run);
            var type = assembly.DefineDynamicModule("model").DefineType("TrimmedModel", TypeAttributes.Public);
            if (hasDefault) type.DefineDefaultConstructor(MethodAttributes.Public);
            var ctor = type.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, new[] { typeof(int) });
            if (attributed) ctor.SetCustomAttribute(new CustomAttributeBuilder(typeof(BsonCtorAttribute).GetConstructor(Type.EmptyTypes), new object[0]));
            var body = ctor.GetILGenerator();
            body.Emit(OpCodes.Ldarg_0);
            body.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes));
            body.Emit(OpCodes.Ret);
            var model = type.CreateTypeInfo().AsType();
            Action deserialize = () => new BsonMapper().ToObject(model, new BsonDocument());
            deserialize.Should().Throw<LiteException>().WithMessage("*TrimmedModel*trimming*")
                .Which.ErrorCode.Should().Be(LiteException.INVALID_CTOR);
        }

        [Fact]
        public void Custom_member_serializer_failure_is_contextual_and_keeps_original_error_code()
        {
            var mapper = new BsonMapper();
            var original = new LiteException(LiteException.INVALID_DATA_TYPE, "custom failure");
            mapper.RegisterType<Broken>(value => throw original, value => new Broken());
            Action serialize = () => mapper.ToDocument(new Parent { Child = new Broken() });
            var error = serialize.Should().Throw<LiteException>().Which;
            error.ErrorCode.Should().Be(original.ErrorCode);
            error.InnerException.Should().BeSameAs(original);
            error.Message.Should().Contain("Parent.Child");
        }

        private static void AssertContextualFailure(Action action, string entity, string member)
        {
            var failure = action.Should().Throw<LiteException>().Which;
            failure.Message.Should().Contain(entity);
            failure.Message.Should().Contain(member);
            failure.InnerException.Should().NotBeNull();
        }
    }
}
