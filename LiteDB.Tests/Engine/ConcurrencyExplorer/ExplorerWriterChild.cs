#nullable disable
using System;
using System.IO;
using LiteDB.Engine;

namespace LiteDB.ConcurrencyTesting
{
    /// <summary>
    /// The external-writer process of the explorer's <c>process=external-writer</c> dimension: a
    /// Shared connection in another process, driven line by line over stdin/stdout so the
    /// controller can force its edges like an actor's. Commands and replies:
    /// <c>write &lt;collection&gt; &lt;id&gt; &lt;value&gt;</c> (auto-commit upsert) and
    /// <c>hold &lt;collection&gt; &lt;id&gt; &lt;value&gt;</c> (BeginTrans + upsert, reply <c>held</c>, then
    /// <c>commit</c> or <c>rollback</c>) reply <c>ok</c> or <c>error &lt;type&gt; &lt;message&gt;</c>;
    /// <c>exit</c> replies <c>bye</c> after the connection closed. Linked into the test harness
    /// process (SharedMutexHarness, mode <c>explorer-writer</c>) and LiteDB.Fuzz
    /// (<c>--child explorer-writer</c>); the password comes from LITEDB_EXPLORER_WRITER_PASSWORD.
    /// </summary>
    internal static class ExplorerWriterChild
    {
        internal const string Mode = "explorer-writer";
        internal const string PasswordVariable = "LITEDB_EXPLORER_WRITER_PASSWORD";

        internal static int Run(string path, TextReader input, TextWriter output)
        {
            var password = Environment.GetEnvironmentVariable(PasswordVariable);
            var settings = new EngineSettings
            {
                Filename = path, Password = string.IsNullOrEmpty(password) ? null : password, TransactionPageLimit = 1
            };
            using (var db = new LiteDatabase(new SharedEngine(settings)))
            {
                output.WriteLine("ready");
                output.Flush();
                string line;
                while ((line = input.ReadLine()) != null)
                {
                    var parts = line.Split(' ');
                    if (parts[0] == "exit") break;
                    output.WriteLine(Execute(db, parts, input, output));
                    output.Flush();
                }
            }
            output.WriteLine("bye");
            output.Flush();
            return 0;
        }

        /// <summary>Same document shape as the explorer model's rows (this file is linked alone into child processes).</summary>
        private static BsonDocument Row(int id, int value) => new BsonDocument
        { ["_id"] = id, ["value"] = value, ["payload"] = new string('x', 8192) };

        private static string Execute(LiteDatabase db, string[] parts, TextReader input, TextWriter output)
        {
            try
            {
                switch (parts[0])
                {
                    case "write":
                        db.GetCollection(parts[1]).Upsert(Row(int.Parse(parts[2]), int.Parse(parts[3])));
                        return "ok";
                    case "hold":
                        db.BeginTrans();
                        db.GetCollection(parts[1]).Upsert(Row(int.Parse(parts[2]), int.Parse(parts[3])));
                        output.WriteLine("held");
                        output.Flush();
                        var decision = input.ReadLine();
                        if (decision == "commit") db.Commit();
                        else db.Rollback();
                        return "ok";
                    default:
                        return "error ArgumentException unknown command " + parts[0];
                }
            }
            catch (Exception error)
            {
                return "error " + error.GetType().FullName + " " + error.Message.Replace('\n', ' ').Replace('\r', ' ');
            }
        }
    }
}
