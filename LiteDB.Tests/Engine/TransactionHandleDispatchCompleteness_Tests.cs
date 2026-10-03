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

        public static IEnumerable<object[]> Cases() =>
            from site in Enum.GetValues(typeof(Site)).Cast<Site>()
            from name in Guarded.Keys
            select new object[] { site, name };

        [Theory]
        [InlineData(Site.Mapper)]
        [InlineData(Site.InputEnumerable)]
        [InlineData(Site.ReadTransform)]
        public void Swallowed_raw_calls_are_all_refused_and_the_handle_stays_active(Site site)
        {
            using var file = new TempFile();
            var results = new Dictionary<string, Exception>();
            Exception unguarded = null;
            RunScenario(file, site, engine =>
            {
                foreach (var entry in Guarded) results[entry.Key] = Record.Exception(() => entry.Value(engine));
                unguarded = Record.Exception(() => engine.GetCollectionNames().ToArray());
            }, (tx, statement) =>
            {
                Assert.Null(statement);
                Assert.Equal(LiteTransactionState.Active, tx.State);
            });
            Assert.Equal(Guarded.Keys.OrderBy(x => x), results.Keys.OrderBy(x => x));
            foreach (var entry in results)
                Assert.True(entry.Value is TransactionCapabilityException,
                    $"{entry.Key} from {site}: {entry.Value?.GetType().Name ?? "ran"} {entry.Value?.Message}");
            Assert.Null(unguarded);
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void Uncaught_raw_call_is_refused_before_side_effects(Site site, string member)
        {
            using var file = new TempFile();
            RunScenario(file, site, engine => Guarded[member](engine), (tx, statement) =>
            {
                Assert.NotNull(statement);
                Assert.True(Unwrap(statement) is TransactionCapabilityException, $"{member} from {site}: {statement}");
                // The mapper reports a failing getter as a LiteException wrapping the refusal.
                if (site == Site.Mapper) Assert.IsType<LiteException>(statement);
                else Assert.IsType<TransactionCapabilityException>(statement);
                // The uncaught refusal fails the statement, which ends the handle; nothing the raw
                // call asked for happened (checked by the scenario).
                Assert.Equal(LiteTransactionState.Failed, tx.State);
            });
        }

        private static Exception Unwrap(Exception error)
        {
            for (var current = error; current != null; current = current.InnerException)
                if (current is TransactionCapabilityException) return current;
            return error;
        }

        public class Entity
        {
            public int Id { get; set; }
            [BsonIgnore] public Action Reading;
            public int Value { get { var reading = Reading; Reading = null; reading?.Invoke(); return 42; } set { } }
        }

        /// <summary>
        /// Seed, run <paramref name="callback"/> raw from the chosen callback site of a bound statement,
        /// check the handle with <paramref name="inspect"/>, roll back, then prove nothing escaped,
        /// on the live database and after two cold reopens.
        /// </summary>
        private static void RunScenario(string file, Site site, Action<LiteEngine> callback, Action<ILiteTransaction, Exception> inspect)
        {
            LiteEngine engine = null;
            var armed = false;
            var settings = new EngineSettings
            {
                Filename = file,
                ReadTransform = (collection, value) =>
                {
                    if (armed && collection == "src") { armed = false; callback(engine); }
                    return value;
                }
            };
            using (engine = new LiteEngine(settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.GetCollection("victim").Insert(new BsonDocument { ["_id"] = 1, ["value"] = 1 });
                db.GetCollection("victim").EnsureIndex("value", "$.value");
                db.GetCollection("src").Insert(new BsonDocument { ["_id"] = 1, ["value"] = 5 });
                var tx = db.BeginTransaction();
                try
                {
                    tx.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 100 });
                    Exception statement;
                    switch (site)
                    {
                        case Site.Mapper:
                            statement = Record.Exception(() => tx.GetCollection<Entity>("rows").Insert(new Entity { Id = 1, Reading = () => callback(engine) }));
                            break;
                        case Site.InputEnumerable:
                            IEnumerable<BsonDocument> Input()
                            {
                                yield return new BsonDocument { ["_id"] = 1 };
                                callback(engine);
                                yield return new BsonDocument { ["_id"] = 2 };
                            }
                            statement = Record.Exception(() => tx.GetCollection("rows").Insert(Input()));
                            break;
                        default:
                            armed = true;
                            statement = Record.Exception(() => tx.GetCollection("src").Query().Into("dst"));
                            Assert.False(armed, "ReadTransform did not run during the bound Into.");
                            break;
                    }
                    inspect(tx, statement);
                    // The peer view during the handle: nothing was published or changed.
                    AssertUntouched(db);
                    if (tx.State == LiteTransactionState.Active) tx.Rollback();
                }
                finally { tx.Dispose(); }
                AssertUntouched(db);
                Assert.Equal(0, db.GetCollection("rows").Count());
                Assert.False(db.CollectionExists("dst"));
            }
            for (var reopen = 0; reopen < 2; reopen++)
            {
                using var cold = new LiteDatabase(file);
                AssertUntouched(cold);
                Assert.False(cold.CollectionExists("rows"));
                Assert.False(cold.CollectionExists("dst"));
            }
        }

        private static void AssertUntouched(LiteDatabase db)
        {
            Assert.False(db.CollectionExists("escape"));
            var victim = db.GetCollection("victim").FindAll().ToArray();
            Assert.Single(victim);
            Assert.Equal(1, victim[0]["value"].AsInt32);
            Assert.Equal(1, db.GetCollection("victim").Count(Query.EQ("value", 1)));
            var indexes = db.GetCollection("$indexes").Find("collection = 'victim'").Select(x => x["name"].AsString).OrderBy(x => x);
            Assert.Equal(new[] { "_id", "value" }, indexes);
            Assert.Equal(0, db.UserVersion);
            Assert.Equal(1, db.GetCollection("src").Count());
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
