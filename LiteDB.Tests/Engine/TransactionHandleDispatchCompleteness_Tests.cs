using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LiteDB.Engine;
using LiteDB.Vector;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// Every public <see cref="LiteEngine"/> and <see cref="ILiteEngine"/> member is either refused
    /// when invoked raw from a bound handle's callback, before any side effect, or is listed as
    /// unguarded with a reason. A new public member fails <see cref="Every_public_engine_member_is_classified"/>
    /// until it is classified here.
    /// </summary>
    public class TransactionHandleDispatchCompleteness_Tests
    {
        public enum Site { Mapper, InputEnumerable, ReadTransform }

        private static BsonDocument Escape(int id) => new BsonDocument { ["_id"] = id, ["value"] = 1000 + id };

        /// <summary>Raw invocations, each with a side effect a cold reopen would show had it run.</summary>
        internal static readonly Dictionary<string, Action<LiteEngine>> Guarded = new Dictionary<string, Action<LiteEngine>>
        {
            ["GetCollectionNames()"] = e => e.GetCollectionNames().ToArray(),
            ["Checkpoint()"] = e => e.Checkpoint(),
            ["Rebuild(RebuildOptions)"] = e => e.Rebuild(new RebuildOptions()),
            ["Rebuild()"] = e => e.Rebuild(), // delegates to the guarded overload
            ["BeginTrans()"] = e => e.BeginTrans(),
            ["Commit()"] = e => e.Commit(),
            ["Rollback()"] = e => e.Rollback(),
            ["Query(String,Query)"] = e => e.Query("victim", new Query()).Dispose(),
            ["Insert(String,IEnumerable<BsonDocument>,BsonAutoId)"] = e => e.Insert("escape", new[] { Escape(1) }, BsonAutoId.Int32),
            ["Update(String,IEnumerable<BsonDocument>)"] = e => e.Update("victim", new[] { new BsonDocument { ["_id"] = 1, ["value"] = 99 } }),
            ["UpdateMany(String,BsonExpression,BsonExpression)"] = e => e.UpdateMany("victim", BsonExpression.Create("{ _id: $._id, value: 99 }"), BsonExpression.Create("_id = 1")),
            ["Upsert(String,IEnumerable<BsonDocument>,BsonAutoId)"] = e => e.Upsert("escape", new[] { Escape(2) }, BsonAutoId.Int32),
            ["Delete(String,IEnumerable<BsonValue>)"] = e => e.Delete("victim", new BsonValue[] { 1 }),
            ["DeleteMany(String,BsonExpression)"] = e => e.DeleteMany("victim", BsonExpression.Create("_id = 1")),
            ["DropCollection(String)"] = e => e.DropCollection("victim"),
            ["RenameCollection(String,String)"] = e => e.RenameCollection("victim", "escape"),
            ["EnsureIndex(String,String,BsonExpression,Boolean)"] = e => e.EnsureIndex("victim", "escape", BsonExpression.Create("$.value"), false),
            ["EnsureVectorIndex(String,String,BsonExpression,VectorIndexOptions)"] = e => e.EnsureVectorIndex("victim", "escapevec", BsonExpression.Create("$.vector"), new VectorIndexOptions(2)),
            ["DropIndex(String,String)"] = e => e.DropIndex("victim", "value"),
            ["Pragma(String)"] = e => e.Pragma(Pragmas.USER_VERSION),
            ["Pragma(String,BsonValue)"] = e => e.Pragma(Pragmas.USER_VERSION, 77),
            ["Dispose()"] = e => e.Dispose(),
        };

        /// <summary>Public members that deliberately skip the dispatch check.</summary>
        private static readonly Dictionary<string, string> Unguarded = new Dictionary<string, string>
        {

        };

        [Fact]
        public void Every_public_engine_member_is_classified()
        {
            var members = typeof(LiteEngine).GetMembers(BindingFlags.Public | BindingFlags.Instance)
                .Concat(typeof(ILiteEngine).GetMembers()).Concat(typeof(IDisposable).GetMembers())
                .Where(member => member.DeclaringType != typeof(object) && !(member is ConstructorInfo))
                .Select(Signature).Distinct().OrderBy(name => name).ToArray();
            var classified = Guarded.Keys.Concat(Unguarded.Keys).ToArray();
            Assert.Empty(Guarded.Keys.Intersect(Unguarded.Keys));
            Assert.True(members.Except(classified).Count() == 0,
                "Classify every public engine member (guarded invoker or unguarded reason): " + string.Join(", ", members.Except(classified)));
            Assert.True(classified.Except(members).Count() == 0, "Stale classification: " + string.Join(", ", classified.Except(members)));
        }

        [Fact]
        public void Every_classified_entry_refuses_raw_handle_callback_reentry()
        {
            using var engine = new LiteEngine();
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            using var transaction = db.BeginTransaction();
            var callbacks = 0;
            IEnumerable<BsonDocument> Input()
            {
                foreach (var entry in Guarded)
                {
                    Assert.Throws<InvalidOperationException>(() => entry.Value(engine));
                    callbacks++;
                }
                yield return Escape(1);
            }
            transaction.GetCollection("rows").Insert(Input());
            Assert.Equal(Guarded.Count, callbacks);
            transaction.Rollback();
            Assert.Empty(db.GetCollection("rows").FindAll());
            Assert.True(TransactionContext.IsThreadClear);
        }

        public static IEnumerable<object[]> ReaderEntries => Guarded.Keys.SelectMany(key =>
            new[] { new object[] { key, false }, new object[] { key, true } });

        [Theory]
        [MemberData(nameof(ReaderEntries))]
        public void Every_classified_entry_refuses_raw_read_transform_reentry(string entry, bool enumerable)
        {
            using var file = new TempFile();
            LiteEngine engine = null;
            var armed = false;
            var callbacks = 0;
            using (engine = new LiteEngine(new EngineSettings
            {
                Filename = file,
                ReadTransform = (collection, value) =>
                {
                    if (armed && collection == "victim" && value["_id"].AsInt32 >= 2)
                    {
                        callbacks++;
                        Assert.Throws<InvalidOperationException>(() => Guarded[entry](engine));
                    }
                    return value;
                }
            }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.GetCollection("victim").Insert(new[] { Escape(1), Escape(2), Escape(3) });
                db.GetCollection("victim").EnsureIndex("value");
                using var transaction = db.BeginTransaction();
                armed = true;
                var query = transaction.GetCollection("victim").Query();
                if (enumerable) Assert.Equal(3, query.ToEnumerable().Count());
                else
                {
                    using var reader = query.ExecuteReader();
                    var count = 0;
                    while (reader.Read()) count++;
                    Assert.Equal(3, count);
                }
                armed = false;
                Assert.Equal(2, callbacks);
                Assert.Equal(LiteTransactionState.Active, transaction.State);
                transaction.GetCollection("legitimate").Insert(Escape(9));
                transaction.Commit();
                using var rollback = db.BeginTransaction();
                rollback.GetCollection("escape").Insert(Escape(10));
                rollback.Rollback();
            }
            for (var pass = 0; pass < 2; pass++)
            {
                using var cold = new LiteDatabase(file);
                Assert.Empty(cold.GetCollection("escape").FindAll());
                Assert.Equal(new[] { 1001, 1002, 1003 }, cold.GetCollection("victim").FindAll().Select(d => d["value"].AsInt32));
                Assert.Equal(3, cold.GetCollection("victim").Count(Query.GTE("value", 1001)));
                Assert.NotNull(cold.GetCollection("legitimate").FindById(9));
                Assert.Equal(0, cold.UserVersion);
                Assert.True(cold.GetCollection("victim").DropIndex("value"));
                cold.GetCollection("victim").EnsureIndex("value");
            }
            Assert.True(TransactionContext.IsThreadClear);
        }

        private static string Signature(MemberInfo member)
        {
            if (!(member is MethodInfo method)) return member.MemberType + ":" + member.Name;
            return method.Name + "(" + string.Join(",", method.GetParameters().Select(p => TypeName(p.ParameterType))) + ")";
        }

        private static string TypeName(Type type) => !type.IsGenericType ? type.Name
            : type.Name.Substring(0, type.Name.IndexOf('`')) + "<" + string.Join(",", type.GetGenericArguments().Select(TypeName)) + ">";
    }
}
