using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Mapper
{
    public class MapperDocumentCapacity_Tests
    {
        [Fact]
        public void Mutable_members_omission_and_order_match_original_pipeline()
        {
            Compare(mapper =>
            {
                var entity = mapper.GetEntityMapper(typeof(Row));
                entity.WaitForInitialization();
                var name = entity.Members.Single(m => m.MemberName == nameof(Row.Name));
                var optional = entity.Members.Single(m => m.MemberName == nameof(Row.Optional));
                mapper.ToDocument(new Row { Id = 1, Name = "warm" });
                entity.Members.Reverse();
                name.FieldName = "renamed";
                name.Serialize = (value, owner) => "custom:" + value;
                optional.Getter = null;
                var doc = mapper.ToDocument(new Row { Id = 2, Name = "next" });
                doc["renamed"].AsString.Should().Be("custom:next");
                doc.ContainsKey("Optional").Should().BeFalse();
                return doc;
            });
            Compare(mapper =>
            {
                mapper.SerializeNullValues = false;
                var entity = mapper.GetEntityMapper(typeof(Row));
                entity.WaitForInitialization();
                var optional = entity.Members.Single(m => m.MemberName == nameof(Row.Optional));
                var name = entity.Members.Single(m => m.MemberName == nameof(Row.Name));
                entity.Members.Remove(name);
                entity.Members.Insert(0, name);
                name.Getter = _ => { optional.Getter = null; return "changed"; };
                var doc = mapper.ToDocument(new Row { Optional = "omit despite non-null" });
                doc.ContainsKey("Optional").Should().BeFalse();
                doc.ContainsKey("_id").Should().BeTrue();
                return doc;
            });
        }

        [Fact]
        public void Custom_serializers_virtual_hooks_and_polymorphism_match_original_pipeline()
        {
            Compare(mapper =>
            {
                mapper.RegisterType<Token>(value => new BsonDocument { ["converted"] = value.Text },
                    value => new Token { Text = value["converted"].AsString });
                var row = new Row { Id = 7, Name = "value", Token = new Token { Text = "token" },
                    Child = new Derived { Label = "child", Extra = 9 } };
                var doc = mapper.ToDocument(typeof(object), row);
                doc.ContainsKey("_type").Should().BeTrue();
                doc["Child"]["_type"].IsString.Should().BeTrue();
                doc["Token"]["converted"].AsString.Should().Be("token");
                doc["hook"].AsBoolean.Should().BeTrue();
                mapper.Deserialize<Row>(doc).Child.Should().BeOfType<Derived>();
                return doc;
            });
        }

        [Fact]
        public void Dictionaries_and_metadata_match_original_pipeline()
        {
            Compare(mapper => mapper.ToDocument(new Dictionary<string, Base>
            {
                ["nested"] = new Derived { Label = "dictionary", Extra = 3 }
            }));
            Compare(mapper => mapper.ToDocument(new Hashtable { ["key"] = 42 }));
            Compare(mapper => mapper.ToDocument(new Metadata
            {
                Callback = () => { }, Member = typeof(Row).GetProperty(nameof(Row.Name))
            }));
            Compare(mapper =>
            {
                mapper.RegisterType<Action>(_ => "delegate", _ => () => { });
                var doc = mapper.ToDocument(new Metadata { Callback = () => { } });
                doc["Callback"].AsString.Should().Be("delegate");
                return doc;
            });
        }

        [Fact]
        public void Getter_failure_keeps_member_diagnostics()
        {
            foreach (var mapper in new HookMapper[] { new HookMapper(), new OriginalMapper() })
            {
                var member = mapper.GetEntityMapper(typeof(Row)).Members.Single(m => m.MemberName == nameof(Row.Name));
                member.Getter = _ => throw new InvalidOperationException("getter failure");
                Action serialize = () => mapper.ToDocument(new Row());
                serialize.Should().Throw<LiteException>().Which.InnerException.Should().BeOfType<InvalidOperationException>();
            }
        }

        [Fact]
        public void Mapped_bulk_documents_and_indexes_survive_rollback_and_reopen()
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
            try
            {
                using (var db = new LiteDatabase(path, new HookMapper()))
                {
                    var rows = db.GetCollection<Row>();
                    rows.EnsureIndex(row => row.Name, true);
                    rows.Insert(Enumerable.Range(1, 40).Select(id => new Row { Id = id, Name = "row" + id }));
                    db.BeginTrans();
                    rows.Insert(new Row { Id = 41, Name = "aborted" });
                    db.Rollback();
                }
                using (var db = new LiteDatabase(path, new HookMapper()))
                {
                    var rows = db.GetCollection<Row>();
                    rows.Count().Should().Be(40);
                    rows.FindOne(row => row.Name == "row20").Id.Should().Be(20);
                    rows.FindById(41).Should().BeNull();
                    rows.FindAll().Select(row => row.Id).OrderBy(id => id).Should().Equal(Enumerable.Range(1, 40));
                }
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
                var log = Path.ChangeExtension(path, "-log.db");
                if (File.Exists(log)) File.Delete(log);
            }
        }

        [Fact]
        public void MapperCapacityHints_CustomSerializerIgnoredAndDuplicateFields_MatchBaseline()
        {
            Compare(mapper =>
            {
                mapper.SerializeNullValues = false;
                var entity = mapper.GetEntityMapper(typeof(Row));
                entity.WaitForInitialization();
                entity.Members.Single(m => m.MemberName == nameof(Row.Name)).FieldName = "alias";
                var optional = entity.Members.Single(m => m.MemberName == nameof(Row.Optional));
                optional.FieldName = "ALIAS";
                optional.Serialize = (value, owner) => "converted:" + value;
                var doc = mapper.ToDocument(new Row { Id = 3, Name = "first", Optional = "last" });
                doc["alias"].AsString.Should().Be("converted:last");
                doc.Keys.Count(key => string.Equals(key, "alias", StringComparison.OrdinalIgnoreCase)).Should().Be(1);
                return doc;
            });
        }

        [Fact]
        public void MapperCapacityHints_ThrowingCallbackAndRollback_PreserveAllIndexes()
        {
            foreach (var mapper in new HookMapper[] { new HookMapper(), new OriginalMapper() })
            {
                var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
                var fail = false;
                var member = mapper.GetEntityMapper(typeof(Row)).Members.Single(m => m.MemberName == nameof(Row.Name));
                member.Serialize = (value, owner) => fail && (string)value == "bad"
                    ? throw new InvalidOperationException("callback failure") : new BsonValue((string)value);
                try
                {
                    using (var db = new LiteDatabase(path, mapper))
                    {
                        var rows = db.GetCollection<Row>();
                        rows.EnsureIndex(row => row.Name, true);
                        rows.EnsureIndex(row => row.Optional);
                        rows.Insert(new Row { Id = 1, Name = "committed", Optional = "keep" });
                        db.BeginTrans();
                        rows.Insert(new Row { Id = 2, Name = "aborted", Optional = "discard" });
                        fail = true;
                        Action insert = () => rows.Insert(new Row { Id = 3, Name = "bad" });
                        insert.Should().Throw<Exception>();
                        db.Rollback();
                    }
                    using (var db = new LiteDatabase(path, mapper))
                    {
                        var rows = db.GetCollection<Row>();
                        rows.FindAll().Select(row => row.Id).Should().Equal(1);
                        rows.FindOne(row => row.Name == "committed").Id.Should().Be(1);
                        rows.FindOne(row => row.Optional == "keep").Id.Should().Be(1);
                        rows.FindOne(row => row.Name == "aborted").Should().BeNull();
                        rows.FindOne(row => row.Optional == "discard").Should().BeNull();
                        var raw = db.GetCollection<BsonDocument>(rows.Name).FindById(1);
                        raw["Name"].AsString.Should().Be("committed");
                        raw["Optional"].AsString.Should().Be("keep");
                    }
                }
                finally
                {
                    if (File.Exists(path)) File.Delete(path);
                }
            }
        }

        private static void Compare(Func<HookMapper, BsonDocument> operation)
        {
            var actualMapper = new HookMapper();
            var originalMapper = new OriginalMapper();
            var actual = operation(actualMapper);
            var expected = operation(originalMapper);
            BsonSerializer.Serialize(actual).Should().Equal(BsonSerializer.Serialize(expected));
            actual.Keys.Should().Equal(expected.Keys);
            actualMapper.Calls.Should().Equal(originalMapper.Calls);
        }

        private class HookMapper : BsonMapper
        {
            public List<string> Calls { get; } = new List<string>();

            public override BsonValue Serialize(Type type, object value, int depth)
            {
                Calls.Add(type.FullName + ":" + depth);
                return base.Serialize(type, value, depth);
            }

            protected override BsonDocument SerializeObject(Type type, object value, int depth)
            {
                var document = base.SerializeObject(type, value, depth);
                if (value is Row) document["hook"] = true;
                return document;
            }
        }

        // Frozen pre-optimization object pipeline; shared member hook retains diagnostic semantics.
        private sealed class OriginalMapper : HookMapper
        {
            protected override BsonDocument SerializeObject(Type type, object value, int depth)
            {
                if (value is Delegate || value is MemberInfo) return null;
                var runtimeType = value.GetType();
                var document = new BsonDocument();
                var entity = GetEntityMapper(runtimeType);
                entity.WaitForInitialization();
                if (type != runtimeType) document["_type"] = SerializeTypeName(runtimeType);
                var hook = typeof(BsonMapper).GetMethod("SerializeMember", BindingFlags.Instance | BindingFlags.NonPublic);
                foreach (var member in entity.Members.Where(member => member.Getter != null))
                {
                    try { hook.Invoke(this, new object[] { document, member, value, depth }); }
                    catch (TargetInvocationException error) { throw error.InnerException; }
                }
                if (value is Row) document["hook"] = true;
                return document;
            }
        }

        public class Row
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public string Optional { get; set; }
            public Token Token { get; set; }
            public Base Child { get; set; }
        }
        public class Token { public string Text { get; set; } }
        public class Base { public string Label { get; set; } }
        public class Derived : Base { public int Extra { get; set; } }
        public class Metadata
        {
            public Action Callback { get; set; }
            public MemberInfo Member { get; set; }
        }
    }
}
