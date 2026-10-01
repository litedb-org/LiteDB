using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using LiteDB;

namespace SharedSelfTeardownProof;

internal static class Fixture
{
    internal static string CanonicalDirectory(string prefix)
    {
        var directory = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        if (Identity == null) return Path.GetFullPath(directory);
        return (string)Identity.GetMethod("CanonicalPath", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { directory })!;
    }

    private static Type? Identity => typeof(LiteDatabase).Assembly.GetTypes().SingleOrDefault(type => type.Name == "DatabaseFileIdentity");

    internal static BsonDocument Row(int id) => new()
    { ["_id"] = id, ["value"] = id, ["payload"] = id >= 100 ? new string('p', 4000) : "row-" + id };

    internal static void Seed(string file, string? password)
    {
        using var db = new LiteDatabase(new ConnectionString { Filename = file, Password = password });
        db.GetCollection("rows").Insert(Row(1));
        db.GetCollection("rows").EnsureIndex("value");
        db.GetCollection("sentinel").Insert(Row(42));
    }

    internal static void Verify(string file, string? password, int[] ids)
    {
        for (var reopen = 0; reopen < 2; reopen++)
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = file, Password = password });
            var rows = db.GetCollection("rows");
            if (!rows.FindAll().OrderBy(row => row["_id"]).Select(row => row.ToString())
                .SequenceEqual(ids.Select(id => Row(id).ToString()))) throw new Exception("Cold exact state differs.");
            foreach (var id in ids)
            {
                var query = rows.Query().Where(Query.EQ("value", id));
                var plan = query.GetPlan()["index"];
                if (plan["name"] != "value" || !plan["mode"].AsString.StartsWith("INDEX SEEK", StringComparison.Ordinal) ||
                    !query.ToArray().Select(row => row.ToString()).SequenceEqual(new[] { Row(id).ToString() }))
                    throw new Exception("Cold indexed payload differs.");
            }
            var sentinel = db.GetCollection("sentinel").FindAll().ToArray();
            if (sentinel.Length != 1 || sentinel[0].ToString() != Row(42).ToString()) throw new Exception("Sentinel changed.");
        }
    }

    internal sealed class CallbackFile : FileStream
    {
        private Action? _callback;
        private CallbackFile(string path) : base(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete) { }
        private CallbackFile(SafeFileHandle handle) : base(handle, FileAccess.ReadWrite) { }
        internal static CallbackFile Open(string path)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX) || Identity == null) return new CallbackFile(path);
            // Match admitted production streams: avoid Darwin's automatic whole-file
            // flock so that the independent probe measures LiteDB native ownership.
            var handle = (SafeFileHandle)Identity.GetMethod("Open", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, new object[] { path, false, true })!;
            try { return new CallbackFile(handle); }
            catch { handle.Dispose(); throw; }
        }
        internal void Arm(Action callback) => Volatile.Write(ref _callback, callback);
        public override void Write(byte[] buffer, int offset, int count)
        { Interlocked.Exchange(ref _callback, null)?.Invoke(); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer)
        { Interlocked.Exchange(ref _callback, null)?.Invoke(); base.Write(buffer); }
    }
}
