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
        private static readonly Dictionary<string, Action<LiteEngine>> Guarded = new Dictionary<string, Action<LiteEngine>>
        {
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
            ["GetCollectionNames()"] = "Reads the committed header's collection list only: no transaction, lock, lease or write.",
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

        private static string Signature(MemberInfo member)
        {
            if (!(member is MethodInfo method)) return member.MemberType + ":" + member.Name;
            return method.Name + "(" + string.Join(",", method.GetParameters().Select(p => TypeName(p.ParameterType))) + ")";
        }

        private static string TypeName(Type type) => !type.IsGenericType ? type.Name
            : type.Name.Substring(0, type.Name.IndexOf('`')) + "<" + string.Join(",", type.GetGenericArguments().Select(TypeName)) + ">";
    }
}
