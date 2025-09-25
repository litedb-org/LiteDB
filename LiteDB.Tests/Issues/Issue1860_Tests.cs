using System;
using System.Collections.Generic;
using Xunit;
using System.Linq;
using LiteDB.Tests.Utils;

namespace LiteDB.Tests.Issues
{
    public class Issue1860_Tests
    {
        [Fact]
        public void Constructor_has_enum_bsonctor()
        {
            using var db = DatabaseFactory.Create();

            // Get a collection (or create, if doesn't exist)
            var col1 = db.GetCollection<C1>("c1");
            var col3 = db.GetCollection<C3>("c3");

            var c1 = new C1
            {
                Id = 1,
                EnumAB = EnumAB.B
            };

            col1.Insert(c1);

            var c3 = new C3
            (
                id: 1,
                enumAB: EnumAB.B
            );

            col3.Insert(c3);

            var value1 = col1.FindAll().FirstOrDefault();
            Assert.NotNull(value1);
            Assert.Equal(c1.EnumAB, value1.EnumAB);

            var value3 = col3.FindAll().FirstOrDefault();
            Assert.NotNull(value3);
            Assert.Equal(c3.EnumAB, value3.EnumAB);
        }

        [Fact]
        public void Constructor_has_enum()
        {
            using var db = DatabaseFactory.Create();

            // Get a collection (or create, if doesn't exist)
            var col1 = db.GetCollection<C1>("c1");
            var col2 = db.GetCollection<C2>("c2");

            var c1 = new C1
            {
                Id = 1,
                EnumAB = EnumAB.B
            };

            col1.Insert(c1);

            var c2 = new C2
            (
                id: 1,
                enumAB: EnumAB.B
            );

            col2.Insert(c2);

            var value1 = col1.FindAll().FirstOrDefault();
            Assert.NotNull(value1);
            Assert.Equal(c1.EnumAB, value1.EnumAB);

            var value2 = col2.FindAll().FirstOrDefault();
            Assert.NotNull(value2);
            Assert.Equal(c2.EnumAB, value2.EnumAB);
        }

        [Fact]
        public void Constructor_has_enum_asint()
        {
            using var db = DatabaseFactory.Create(mapper: new BsonMapper { EnumAsInteger = true });

            // Get a collection (or create, if doesn't exist)
            var col1 = db.GetCollection<C1>("c1");
            var col2 = db.GetCollection<C2>("c2");

            var c1 = new C1
            {
                Id = 1,
                EnumAB = EnumAB.B
            };

            col1.Insert(c1);

            var c2 = new C2
            (
                id: 1,
                enumAB: EnumAB.B
            );

            col2.Insert(c2);

            var value1 = col1.FindAll().FirstOrDefault();
            Assert.NotNull(value1);
            Assert.Equal(c1.EnumAB, value1.EnumAB);

            var value2 = col2.FindAll().FirstOrDefault();
            Assert.NotNull(value2);
            Assert.Equal(c2.EnumAB, value2.EnumAB);
        }

        public enum EnumAB
        {
            
            A = 1,
            B = 2,
        }

        public class C1
        {
            public int Id { get; set; }

            public EnumAB? EnumAB { get; set; }
        }

        public class C2
        {
            public int Id { get; set; }

            public EnumAB EnumAB { get; set; }

            public C2(int id, EnumAB enumAB)
            {
                Id = id;
                EnumAB = enumAB;
            }
        }

        public class C3
        {
            public int Id { get; set; }

            public EnumAB EnumAB { get; set; }

            [BsonCtor]
            public C3(int id, EnumAB enumAB)
            {
                Id = id;
                EnumAB = enumAB;
            }
        }
    }
}
