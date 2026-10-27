#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Tests;

namespace LiteDB.Internals
{
    internal sealed class SharedOneFilePublicationFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(SharedMappedDirectory.Root, "litedb-shm-fix-" + Guid.NewGuid().ToString("N"));
        internal string Filename => Path.Combine(_directory, "test.db");
        internal string Log => FileHelper.GetLogFile(Filename);
        private readonly string _password;
        private readonly CompactStorageMode _compact;
        internal EngineSettings Settings => new EngineSettings { Filename = Filename, SharedOneFile = true, Password = _password, CompactStorage = _compact };
        internal SharedOneFilePublicationFixture(string password = null, bool legacy = false)
        {
            _password = password;
            _compact = legacy ? CompactStorageMode.Legacy : CompactStorageMode.Auto;
            Directory.CreateDirectory(_directory);
            using var seed = new LiteDatabase(new LiteEngine(new EngineSettings { Filename = Filename, Password = password, CompactStorage = _compact }));
            seed.GetCollection("docs").Insert(Enumerable.Range(1, 160).Select(id => Row(id, 0)));
            seed.GetCollection("docs").EnsureIndex("value");
            seed.GetCollection("sentinel").Insert(Row(42, 9));
            seed.GetCollection("sentinel").EnsureIndex("value");
        }
        internal static BsonDocument Row(int id, int value) => new BsonDocument
        { ["_id"] = id, ["value"] = value, ["payload"] = new string('x', 1024) };
        internal static T Field<T>(object value, string name) =>
            (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value);
        internal static LiteEngine Core(SharedEngine engine) => Field<LiteEngine>(engine, "_engine");
        internal static EngineState State(LiteEngine core) => Field<EngineState>(core, "_state");
        internal static void Verify(LiteDatabase db, int first)
        {
            var expected = Enumerable.Range(1, 160).Select(id => Row(id, id == 1 ? first : 0)).ToArray();
            db.GetCollection("docs").FindAll().Should().BeEquivalentTo(expected);
            db.GetCollection("docs").Find(Query.EQ("value", 0)).Should().BeEquivalentTo(expected.Where(d => d["value"] == 0));
            if (first != 0) db.GetCollection("docs").Find(Query.EQ("value", first)).Should().BeEquivalentTo(new[] { Row(1, first) });
            db.GetCollection("sentinel").FindAll().Should().BeEquivalentTo(new[] { Row(42, 9) });
            db.GetCollection("sentinel").Find(Query.EQ("value", 9)).Should().BeEquivalentTo(new[] { Row(42, 9) });
        }
        internal static void Quiet(SharedMemoryReaderTable table)
        {
            // Healthy idle cores now retain WRITE. Request an actual boundary
            // handoff before inspecting the same quiescent publication contract.
            using var request = table.Authority.Requests.Enter();
            using var main = SharedMutexFactory.Create(SharedMutexNameFactory.Create(table.Settings.Filename, table.Settings.SharedMutexNameStrategy));
            var held = main.WaitOne(TimeSpan.FromSeconds(5));
            held.Should().BeTrue("the requested tenure must return main after closing storage and WRITE");
            try
            {
                using var probe = new SharedRangeLocks(SharedMemoryAuthority.PathFor(table.Settings.Filename));
                probe.EnterWrite(); probe.ExitWrite();
                var map = table.Authority.Map;
                (map.Load(SharedMemoryLayout.Control + 8) & 1).Should().Be(0);
                (map.Load(SharedMemoryLayout.StructuralEpoch) & 1).Should().Be(0);
                map.Load(SharedMemoryLayout.RecoveryOwner).Should().Be(0);
                map.Load(SharedMemoryLayout.WriterOwner).Should().Be(0);
                SharedMemoryReaderTable.Scan(table.Authority).Should().NotBeNull();
            }
            finally { if (held) main.ReleaseMutex(); }
        }
        internal static void Fenced(SharedMemoryReaderTable table)
        {
            (table.Authority.Map.Load(SharedMemoryLayout.Control + 8) & 1).Should().Be(1);
            table.Authority.Map.Load(SharedMemoryLayout.RecoveryOwner).Should().NotBe(0);
            SharedMemoryReaderTable.Scan(table.Authority).Should().BeNull();
        }
        internal void WriteIsFree()
        {
            using var probe = new SharedRangeLocks(SharedMemoryAuthority.PathFor(Filename));
            probe.EnterWrite();
            probe.ExitWrite();
        }
        public void Dispose() => WindowsFileRelease.DeleteDirectory(_directory);
    }
}
#endif
