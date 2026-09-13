using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteDB.Shell
{
    internal interface IShellCommand
    {
        bool IsCommand(StringScanner s);

        void Execute(StringScanner s, Env env);
    }
}