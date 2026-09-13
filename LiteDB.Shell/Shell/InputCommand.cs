using System;
using System.Collections.Generic;

namespace LiteDB.Shell
{
    public class InputCommand
    {
        public Queue<string> Queue { get; set; }
        public List<string> History { get; set; }
        public bool Running { get; set; }
        public bool AutoExit { get; set; }

        public InputCommand()
        {
            this.Queue = new Queue<string>();
            this.History = new List<string>();
            this.Running = true;
            this.AutoExit = false; // run "exit" command when there is not more command in queue
        }

        public string ReadCommand()
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write("> ");

            var cmd = this.ReadLine();

            if (cmd == null)
            {
                this.AutoExit = true;
                this.Running = false;
                return "";
            }

            cmd = cmd.Trim();

            // single line only for shell commands
            if (ShellProgram.GetCommand(cmd) == null)
            {
                while (!cmd.EndsWith(";"))
                {
                    Console.ForegroundColor = ConsoleColor.White;
                    Console.Write("| ");

                    var line = this.ReadLine();
                    cmd += Environment.NewLine + line;
                }
            }

            cmd = cmd.Trim();

            this.History.Add(cmd);

            return cmd.Trim();
        }

        /// <summary>
        /// Read a line from queue or user
        /// </summary>
        private string ReadLine()
        {
            Console.ForegroundColor = ConsoleColor.Gray;

            if (this.Queue.Count > 0)
            {
                var cmd = this.Queue.Dequeue();
                Console.Write(cmd + Environment.NewLine);
                return cmd;
            }
            else
            {
                if (this.AutoExit) return "exit";

                return Console.ReadLine();
            }
        }
    }
}