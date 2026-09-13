using System;

namespace LiteDB.Shell.Commands
{
    [Help(
        Name = "quit",
        Syntax = "quit|exit",
        Description = "Close shell application"
    )]
    internal class Quit : IShellCommand
    {
        public bool IsCommand(StringScanner s)
        {
            return s.Match(@"(quit|exit)$");
        }

        public void Execute(StringScanner s, Env env)
        {
            env.Database?.Dispose();
            env.Input.Running = false;
        }
    }
}