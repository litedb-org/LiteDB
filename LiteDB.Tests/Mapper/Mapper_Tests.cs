using FluentAssertions;
using System;
using System.Reflection;
using LiteDB.Tests.Utils;
using Xunit;

namespace LiteDB.Tests.Mapper
{
    public class Mapper_Tests
    {
        private readonly BsonMapper _mapper = new BsonMapper();

        [Fact]
        public void ToDocument_RejectsNonDocumentRootsWithTypeContext()
        {
            var array = new int[] { 1, 2, 3, 4, 5 };
            Action generic = () => _mapper.ToDocument(array);
            Action typed = () => _mapper.ToDocument(typeof(int[]), array);
            generic.Should().Throw<LiteException>().WithMessage("*System.Int32[]*root document*");
            typed.Should().Throw<LiteException>().WithMessage("*System.Int32[]*root document*");
        }

        [Fact]
        public void Class_Not_Assignable()
        {
            using (var db = DatabaseFactory.Create())
            {
                var col = db.GetCollection<MyClass>("Test");
                col.Insert(new MyClass { Id = 1, Member = null });
                var type = typeof(OtherClass);
                var typeName = type.FullName + ", " + type.GetTypeInfo().Assembly.GetName().Name;

                db.Execute($"update Test set Member = {{_id: 1, Name: null, _type: \"{typeName}\"}} where _id = 1");

                Func<MyClass> func = (() => col.FindById(1));
                func.Should().Throw<LiteException>();
            }
        }

        public class MyClass
        {
            public int Id { get; set; }
            public MyClass Member { get; set; }
        }

        public class OtherClass
        {
            public string Name { get; set; }
        }
    }
}
