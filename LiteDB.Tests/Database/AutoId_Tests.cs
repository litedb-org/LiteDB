using System;
using System.IO;
using System.Linq;
using LiteDB;
using AwesomeAssertions;
using Xunit;

namespace LiteDB.Tests.Database
{
    public class AutoId_Tests
    {
        #region Model

        public class EntityInt
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }

        public class EntityLong
        {
            public long Id { get; set; }
            public string Name { get; set; }
        }

        public class EntityGuid
        {
            public Guid Id { get; set; }
            public string Name { get; set; }
        }

        public class EntityOid
        {
            public ObjectId Id { get; set; }
            public string Name { get; set; }
        }

        public class EntityString
        {
            public string Id { get; set; }
            public string Name { get; set; }
        }

        #endregion

        [Fact]
        public void AutoId_Strong_Typed()
        {
            var mapper = new BsonMapper();

            using (var db = new LiteDatabase(new MemoryStream(), mapper, new MemoryStream()))
            {
                var cs_int = db.GetCollection<EntityInt>("int");
                var cs_long = db.GetCollection<EntityLong>("long");
                var cs_guid = db.GetCollection<EntityGuid>("guid");
                var cs_oid = db.GetCollection<EntityOid>("oid");
                var cs_str = db.GetCollection<EntityString>("str");

                // int32
                var cint_1 = new EntityInt() { Name = "R1" };
                var cint_2 = new EntityInt() { Name = "R2" };
                var cint_3 = new EntityInt() { Name = "R3" };
                var cint_4 = new EntityInt() { Name = "R4" };

                // long
                var clong_1 = new EntityLong() { Name = "R1" };
                var clong_2 = new EntityLong() { Name = "R2" };
                var clong_3 = new EntityLong() { Name = "R3" };
                var clong_4 = new EntityLong() { Name = "R4" };

                // guid
                var cguid_1 = new EntityGuid() { Name = "R1" };
                var cguid_2 = new EntityGuid() { Name = "R2" };
                var cguid_3 = new EntityGuid() { Name = "R3" };
                var cguid_4 = new EntityGuid() { Name = "R4" };

                // oid
                var coid_1 = new EntityOid() { Name = "R1" };
                var coid_2 = new EntityOid() { Name = "R2" };
                var coid_3 = new EntityOid() { Name = "R3" };
                var coid_4 = new EntityOid() { Name = "R4" };

                // string - there is no AutoId for string
                var cstr_1 = new EntityString() { Id = "a", Name = "R1" };
                var cstr_2 = new EntityString() { Id = "b", Name = "R2" };
                var cstr_3 = new EntityString() { Id = "c", Name = "R3" };
                var cstr_4 = new EntityString() { Id = "d", Name = "R4" };

                // insert first 3 documents
                cs_int.Insert(new[] { cint_1, cint_2, cint_3 });
                cs_long.Insert(new[] { clong_1, clong_2, clong_3 });
                cs_guid.Insert(new[] { cguid_1, cguid_2, cguid_3 });
                cs_oid.Insert(new[] { coid_1, coid_2, coid_3 });
                cs_str.Insert(new[] { cstr_1, cstr_2, cstr_3 });

                // change document 2
                cint_2.Name = "Changed 2";
                clong_2.Name = "Changed 2";
                cguid_2.Name = "Changed 2";
                coid_2.Name = "Changed 2";
                cstr_2.Name = "Changed 2";

                // update document 2
                var nu_int = cs_int.Update(cint_2);
                var nu_long = cs_long.Update(clong_2);
                var nu_guid = cs_guid.Update(cguid_2);
                var nu_oid = cs_oid.Update(coid_2);
                var nu_str = cs_str.Update(cstr_2);

                nu_int.Should().BeTrue();
                nu_long.Should().BeTrue();
                nu_guid.Should().BeTrue();
                nu_oid.Should().BeTrue();
                nu_str.Should().BeTrue();

                // change document 3
                cint_3.Name = "Changed 3";
                clong_3.Name = "Changed 3";
                cguid_3.Name = "Changed 3";
                coid_3.Name = "Changed 3";
                cstr_3.Name = "Changed 3";

                // upsert (update) document 3
                var fu_int = cs_int.Upsert(cint_3);
                var fu_long = cs_long.Upsert(clong_3);
                var fu_guid = cs_guid.Upsert(cguid_3);
                var fu_oid = cs_oid.Upsert(coid_3);
                var fu_str = cs_str.Upsert(cstr_3);

                fu_int.Should().BeFalse();
                fu_long.Should().BeFalse();
                fu_guid.Should().BeFalse();
                fu_oid.Should().BeFalse();
                fu_str.Should().BeFalse();

                // test if was changed
                cs_int.FindOne(x => x.Id == cint_3.Id).Name.Should().Be(cint_3.Name);
                cs_long.FindOne(x => x.Id == clong_3.Id).Name.Should().Be(clong_3.Name);
                cs_guid.FindOne(x => x.Id == cguid_3.Id).Name.Should().Be(cguid_3.Name);
                cs_oid.FindOne(x => x.Id == coid_3.Id).Name.Should().Be(coid_3.Name);
                cs_str.FindOne(x => x.Id == cstr_3.Id).Name.Should().Be(cstr_3.Name);

                // upsert (insert) document 4
                var tu_int = cs_int.Upsert(cint_4);
                var tu_long = cs_long.Upsert(clong_4);
                var tu_guid = cs_guid.Upsert(cguid_4);
                var tu_oid = cs_oid.Upsert(coid_4);
                var tu_str = cs_str.Upsert(cstr_4);

                tu_int.Should().BeTrue();
                tu_long.Should().BeTrue();
                tu_guid.Should().BeTrue();
                tu_oid.Should().BeTrue();
                tu_str.Should().BeTrue();

                // test if was included
                cs_int.FindOne(x => x.Id == cint_4.Id).Name.Should().Be(cint_4.Name);
                cs_long.FindOne(x => x.Id == clong_4.Id).Name.Should().Be(clong_4.Name);
                cs_guid.FindOne(x => x.Id == cguid_4.Id).Name.Should().Be(cguid_4.Name);
                cs_oid.FindOne(x => x.Id == coid_4.Id).Name.Should().Be(coid_4.Name);
                cs_str.FindOne(x => x.Id == cstr_4.Id).Name.Should().Be(cstr_4.Name);

                // count must be 4
                cs_int.Count(Query.All()).Should().Be(4);
                cs_long.Count(Query.All()).Should().Be(4);
                cs_guid.Count(Query.All()).Should().Be(4);
                cs_oid.Count(Query.All()).Should().Be(4);
                cs_str.Count(Query.All()).Should().Be(4);

                // for Int32 (or Int64) - add "bouble" on sequence
                var cint_10 = new EntityInt { Id = 10, Name = "R10" };
                var cint_11 = new EntityInt { Name = "R11" };
                var cint_7 = new EntityInt { Id = 7, Name = "R7" };
                var cint_12 = new EntityInt { Name = "R12" };

                cs_int.Insert(cint_10); // "loose" sequente between 5-9
                cs_int.Insert(cint_11); // insert as 11
                cs_int.Insert(cint_7); // insert as 7
                cs_int.Insert(cint_12); // insert as 12

                cint_10.Id.Should().Be(10);
                cint_11.Id.Should().Be(11);
                cint_7.Id.Should().Be(7);
                cint_12.Id.Should().Be(12);
            }
        }

        [Fact]
        public void AutoId_BsonDocument()
        {
            using (var db = new LiteDatabase(new MemoryStream()))
            {
                var col = db.GetCollection("Writers");
                col.Insert(new BsonDocument { ["Name"] = "Mark Twain" });
                col.Insert(new BsonDocument { ["Name"] = "Jack London", ["_id"] = 1 });

                // create an index in name field
                col.EnsureIndex("LowerName", "LOWER($.Name)");

                var mark = col.FindOne(Query.EQ("LOWER($.Name)", "mark twain"));
                var jack = col.FindOne(Query.EQ("LOWER($.Name)", "jack london"));

                // checks if auto-id is a ObjectId
                mark["_id"].IsObjectId.Should().BeTrue();
                jack["_id"].IsInt32.Should().BeTrue(); // jack do not use AutoId (fixed in int32)
            }
        }

        [Fact]
        public void AutoId_No_Duplicate_After_Delete()
        {
            // using strong type
            using (var db = new LiteDatabase(new MemoryStream()))
            {
                var col = db.GetCollection<EntityInt>("col1");

                var one = new EntityInt { Name = "One" };
                var two = new EntityInt { Name = "Two" };
                var three = new EntityInt { Name = "Three" };
                var four = new EntityInt { Name = "Four" };

                // insert
                col.Insert(one);
                col.Insert(two);

                one.Id.Should().Be(1);
                two.Id.Should().Be(2);

                // now delete first 2 rows
                col.Delete(one.Id);
                col.Delete(two.Id);

                // and insert new documents
                col.Insert(new EntityInt[] { three, four });

                three.Id.Should().Be(3);
                four.Id.Should().Be(4);
            }

            // using bsondocument/engine
            using (var db = new LiteDatabase(new MemoryStream()))
            {
                var one = new BsonDocument { ["Name"] = "One" };
                var two = new BsonDocument { ["Name"] = "Two" };
                var three = new BsonDocument { ["Name"] = "Three" };
                var four = new BsonDocument { ["Name"] = "Four" };

                var col = db.GetCollection("col", BsonAutoId.Int32);

                col.Insert(one);
                col.Insert(two);

                one["_id"].AsInt32.Should().Be(1);
                two["_id"].AsInt32.Should().Be(2);

                // now delete first 2 rows
                col.Delete(one["_id"].AsInt32);
                col.Delete(two["_id"].AsInt32);

                // and insert new documents
                col.Insert(new BsonDocument[] { three, four });

                three["_id"].AsInt32.Should().Be(3);
                four["_id"].AsInt32.Should().Be(4);
            }
        }

        [Fact]
        public void AutoId_Zero_Int()
        {
            using (var db = new LiteDatabase(":memory:"))
            {
                var test = db.GetCollection("Test", BsonAutoId.Int32);
                var doc = new BsonDocument() { ["_id"] = 0, ["p1"] = 1 };
                test.Insert(doc); // -> NullReferenceException
            }
        }

        [Fact]
        public void AutoId_property()
        {
            using (var db = new LiteDatabase(new MemoryStream()))
            {
                // default auto id
                var col1 = db.GetCollection("Col1");
                col1.AutoId.Should().Be(BsonAutoId.ObjectId);

                // specified auto id
                var col2 = db.GetCollection("Col2", BsonAutoId.Int32);
                col2.AutoId.Should().Be(BsonAutoId.Int32);
            }
        }
    }
}
