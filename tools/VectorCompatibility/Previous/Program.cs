using System;
using System.IO;
using System.Linq;
using System.Reflection;
using LiteDB;

var version = typeof(LiteDatabase).Assembly.GetType("LiteDB.Engine.HeaderPage")!
    .GetField("CURRENT_FILE_VERSION", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!
    .GetRawConstantValue();
if (Convert.ToInt32(version) != 12) throw new Exception("The previous engine must support v12, not v13.");
foreach (var encrypted in new[] { false, true })
{
    var path = Path.Combine(args[0], "reclaimed-" + (encrypted ? "encrypted.db" : "plain.db"));
    var log = Path.Combine(args[0], Path.GetFileNameWithoutExtension(path) + "-log.db");
    var dataBefore = File.ReadAllBytes(path);
    var logBefore = File.ReadAllBytes(log);
    foreach (var mode in new[] { "read-only", "write", "rebuild", "upgrade" })
    {
        var rejected = false;
        try
        {
            using var db = new LiteDatabase(new ConnectionString
            {
                Filename = path, Password = encrypted ? "compatibility-test" : null,
                ReadOnly = mode == "read-only", Upgrade = mode == "upgrade"
            });
            if (mode == "rebuild") db.Rebuild();
            _ = db.GetCollection("cold").Count();
        }
        catch (LiteException error)
        {
            if (!error.Message.Contains("version", StringComparison.OrdinalIgnoreCase)) throw;
            rejected = true;
        }
        if (!rejected || !dataBefore.SequenceEqual(File.ReadAllBytes(path)) ||
            !logBefore.SequenceEqual(File.ReadAllBytes(log)))
            throw new Exception($"v12 engine did not reject {mode} without changing source files (encrypted={encrypted}).");
    }
}
Console.WriteLine("Previous v12 engine: all eight rejection/byte-preservation probes passed.");
