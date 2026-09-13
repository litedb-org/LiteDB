using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LiteDB.Shell.Commands
{
    [Help(
        Name = "open",
        Syntax = "open <filename|connectionString>",
        Description = "Open (or create) a new datafile. Can be used a single filename or a connection string with all supported parameters.",
        Examples = new string[] {
            "open mydb.db",
            "open filename=mydb.db; password=johndoe; initial=100Mb"
        }
    )]
    internal class Open : IShellCommand
    {
        public bool IsCommand(StringScanner s)
        {
            return s.Scan(@"open\s+").Length > 0;
        }

        public void Execute(StringScanner s, Env env)
        {
            var connectionString = new ConnectionString(s.Scan(@".+").TrimToNull());

            if (env.Database != null)
            {
                env.Database.Dispose();
                env.Database = null;
            }

            env.Database = new LiteDatabase(connectionString);
        }
    }
}