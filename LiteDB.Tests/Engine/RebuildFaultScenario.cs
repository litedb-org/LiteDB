using System;
using System.Collections.Generic;
using LiteDB.Engine;

namespace LiteDB.Tests.Engine
{
    public enum RebuildChange { None, SetPassword, RemovePassword, Collation }

    public sealed class RebuildFaultScenario
    {
        private const string Password = "rebuild-password";

        public RebuildFaultScenario(RebuildChange change, bool wal, ConnectionType connection)
        {
            this.Change = change;
            this.Wal = wal;
            this.Connection = connection;
        }

        public RebuildChange Change { get; }
        public bool Wal { get; }
        public ConnectionType Connection { get; }

        public RebuildOptions CreateOptions()
        {
            switch (this.Change)
            {
                case RebuildChange.SetPassword: return new RebuildOptions { Password = Password };
                case RebuildChange.RemovePassword: return new RebuildOptions { RemovePassword = true };
                case RebuildChange.Collation: return new RebuildOptions { Collation = new Collation("en-US/IgnoreCase") };
                default: return new RebuildOptions();
            }
        }

        public ConnectionString OriginalConnection(string filename) => new ConnectionString
        {
            Filename = filename,
            Password = this.Change == RebuildChange.RemovePassword ? Password : null,
            Collation = new Collation("en-US/None")
        };

        public ConnectionString ReplacementConnection(string filename) => new ConnectionString
        {
            Filename = filename,
            Password = this.Change == RebuildChange.SetPassword ? Password
                : this.Change == RebuildChange.RemovePassword ? null
                : this.OriginalConnection(filename).Password,
            Collation = this.Change == RebuildChange.Collation ? new Collation("en-US/IgnoreCase") : new Collation("en-US/None")
        };

        /// <summary>Everything that decides file contents; the connection type does not.</summary>
        public string Key => $"{this.Change}/{(this.Wal ? "wal" : "nowal")}";

        private static readonly Dictionary<string, byte[]> Replacements = new Dictionary<string, byte[]>();

        /// <summary>The checkpointed database a successful rebuild of this scenario publishes.</summary>
        public byte[] Replacement()
        {
            lock (Replacements)
            {
                if (Replacements.TryGetValue(this.Key, out var bytes)) return bytes;

                using (var run = new RebuildFaultRun(new RebuildFaultScenario(this.Change, this.Wal, ConnectionType.Direct), new string[0]))
                {
                    run.Execute();
                    if (run.Failure != null) throw new InvalidOperationException("could not build the replacement", run.Failure);
                    bytes = run.ReadLive();
                }

                return Replacements[this.Key] = bytes;
            }
        }

        public override string ToString() => $"{this.Key}/{this.Connection}";
    }
}
