using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using LiteDB.Shell.Commands;

namespace LiteDB.Shell
{
    internal class ShellProgram
    {
        public static void Start(InputCommand input, Display display)
        {
            var env = new Env { Input = input, Display = display };

            // show welcome message
            display.WriteWelcome();

            Console.CancelKeyPress += (o, e) => { e.Cancel = true; env.Running = false; };

            while (input.Running)
            {
                // read next command from user or queue
                var cmd = input.ReadCommand();

                if (string.IsNullOrEmpty(cmd)) continue;

                try
                {
                    var scmd = GetCommand(cmd);

                    if (scmd != null)
                    {
                        scmd(env);
                        continue;
                    }

                    // if string is not a shell command, try execute as sql command
                    if (env.Database == null) throw new Exception("Database not connected");

                    env.Running = true;

                    display.WriteResult(env.Database.Execute(cmd), env);

                }
                catch (Exception ex)
                {
                    display.WriteError(ex);
                }
            }
        }

        #region Shell Commands

        private static readonly List<IShellCommand> _commands = new List<IShellCommand>();

        static ShellProgram()
        {
            var type = typeof(IShellCommand);
            var types = typeof(ShellProgram).Assembly
                .GetTypes()
                .Where(p => type.IsAssignableFrom(p) && p.IsClass);

            foreach (var cmd in types)
            {
                _commands.Add(Activator.CreateInstance(cmd) as IShellCommand);
            }
        }

        public static Action<Env> GetCommand(string cmd)
        {
            var s = new StringScanner(cmd);

            // first test all shell app commands
            foreach (var command in _commands)
            {
                if (!command.IsCommand(s)) continue;

                return (env) => command.Execute(s, env);
            }

            return null;
        }

        #endregion
    }
}