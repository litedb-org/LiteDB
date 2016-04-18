﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace LiteDB.Shell
{
    internal class LiteShell
    {
        private static List<IShellCommand> _commands = new List<IShellCommand>();

        static LiteShell()
        {
            var type = typeof(IShellCommand);
#if !PORTABLE
            var types = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(s => s.GetTypes())
                .Where(p => type.IsAssignableFrom(p) && p.IsClass);
#else
            var types = typeof(LiteShell).GetTypeInfo().Assembly.GetTypes()
                .Where(p => type.IsAssignableFrom(p) && p.GetTypeInfo().IsClass);
#endif
            foreach (var t in types)
            {
                var cmd = (IShellCommand)Activator.CreateInstance(t);
                _commands.Add(cmd);
            }
        }

        public BsonValue Run(DbEngine engine, string command)
        {
            if (string.IsNullOrEmpty(command)) return BsonValue.Null;

            var s = new StringScanner(command);

            foreach (var cmd in _commands)
            {
                if (cmd.IsCommand(s))
                {
                    return cmd.Execute(engine, s);
                }
            }

            throw LiteException.InvalidCommand(command);
        }
    }
}