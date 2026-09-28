using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class SharedAdmissionLifetime_Tests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public async Task Protected_connection_retains_admission_between_operations_and_until_last_reader(string password)
        {
            using var file = new TempFile();
            using (var seed = new LiteDatabase(new ConnectionString { Filename = file.Filename, Password = password }))
            {
                seed.GetCollection("rows").InsertBulk(Enumerable.Range(0, 3000)
                    .Select(id => new BsonDocument { ["_id"] = id, ["value"] = id * 2 }));
                seed.GetCollection("rows").EnsureIndex("value");
                seed.GetCollection("untouched").Insert(new BsonDocument { ["_id"] = 1, ["value"] = 42 });
            }
            AppContext.TryGetSwitch(SharedCoordinationPolicy.DisableMappedSwitch, out var before);
            SharedEngine engine;
            AppContext.SetSwitch(SharedCoordinationPolicy.DisableMappedSwitch, true);
            try { engine = new SharedEngine(new EngineSettings { Filename = file.Filename, Password = password }); }
            finally { AppContext.SetSwitch(SharedCoordinationPolicy.DisableMappedSwitch, before); }
            using (engine)
            using (var db = new LiteDatabase(engine))
            {
                var rows = db.GetCollection("rows");
                for (var i = 0; i < 5; i++) rows.FindById(i)["value"].AsInt32.Should().Be(i * 2);
                Action direct = () => { using var writer = new LiteEngine(new EngineSettings { Filename = file.Filename, Password = password }); };
                direct.Should().Throw<DatabaseAdmissionException>("an idle admitted connection still excludes incompatible writers");
                using (var other = new LiteDatabase(new SharedEngine(new EngineSettings
                { Filename = file.Filename, Password = password, SharedMutexNameStrategy = SharedMutexNameStrategy.Sha1Hash })))
                {
                    Action write = () => other.GetCollection("rows").DeleteAll();
                    write.Should().Throw<DatabaseAdmissionException>();
                }
                using var first = rows.FindAll().GetEnumerator();
                using var last = rows.FindAll().GetEnumerator();
                first.MoveNext().Should().BeTrue();
                last.MoveNext().Should().BeTrue();
                engine.Dispose();
                engine.Dispose();
                await Task.Run(() => first.Dispose());
                direct.Should().Throw<DatabaseAdmissionException>("the remaining reader owns admission after connection disposal");
                var count = 0;
                do
                {
                    last.Current["value"].AsInt32.Should().Be(last.Current["_id"].AsInt32 * 2);
                    count++;
                } while (last.MoveNext());
                count.Should().Be(3000);
                await Task.Run(() => last.Dispose());
                direct.Should().NotThrow("the last reader releases admission");
            }
            using var cold = new LiteDatabase(new ConnectionString { Filename = file.Filename, Password = password });
            cold.GetCollection("rows").Find("value >= 0").Count().Should().Be(3000);
            cold.GetCollection("untouched").FindById(1)["value"].AsInt32.Should().Be(42);
        }

        [Fact]
        public void Open_retains_admission_when_a_callback_disposes_the_connection()
        {
            using var file = new TempFile();
            using (var seed = new LiteDatabase(file.Filename))
                seed.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = 42 });
            using var engine = new SharedEngine(new EngineSettings { Filename = file.Filename });
            Action direct = () => { using var writer = new LiteEngine(file.Filename); };
            engine.SimulateOpenEngine = () =>
            {
                engine.Dispose();
                direct.Should().Throw<DatabaseAdmissionException>("setup still owns admission after reentrant connection disposal");
                throw new IOException("injected open failure");
            };
            Action open = () => engine.Pragma(Pragmas.USER_VERSION);
            open.Should().Throw<IOException>().WithMessage("injected open failure");
            direct.Should().NotThrow("failed setup releases its temporary reference");
            using var cold = new LiteDatabase(file.Filename);
            cold.GetCollection("rows").FindById(1)["value"].AsInt32.Should().Be(42);
        }

        [Fact]
        public void Failed_inner_opens_release_their_references_and_connection_disposal_releases_admission()
        {
            using var file = new TempFile();
            using (var seed = new LiteDatabase(new ConnectionString { Filename = file.Filename, Password = "secret" }))
                seed.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = 42 });
            using (var rejected = new SharedEngine(new EngineSettings { Filename = file.Filename, Password = "wrong" }))
            {
                for (var i = 0; i < 3; i++)
                {
                    Action open = () => rejected.Pragma(Pragmas.USER_VERSION);
                    open.Should().Throw<LiteException>();
                }
            }
            using var cold = new LiteDatabase(new ConnectionString { Filename = file.Filename, Password = "secret" });
            cold.GetCollection("rows").FindById(1)["value"].AsInt32.Should().Be(42);
        }
    }
}
