using System;
using System.Linq;

namespace LiteDB.Shell.Commands
{
    [Help(
        Name = "show collections",
        Syntax = "show collections",
        Description = "List all collections inside datafile."
    )]
    internal class ShowCollections : IShellCommand
    {
        public bool IsCommand(StringScanner s)
        {
            return s.Match(@"show\scollections$");
        }

        public void Execute(StringScanner s, Env env)
        {
            if (env.Database == null) throw new Exception("Database not connected");

            var cols = env.Database.GetCollectionNames().OrderBy(x => x).ToArray();

            if (cols.Length > 0)
            {
                env.Display.WriteLine(ConsoleColor.Cyan, string.Join(Environment.NewLine, cols));
            }
        }
    }
}