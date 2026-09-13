using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace LiteDB.Shell
{
    internal class Display
    {
        public bool Pretty { get; set; }

        public Display()
        {
            this.Pretty = false;
        }

        public void WriteWelcome()
        {
            this.WriteInfo("Welcome to LiteDB Shell");
            this.WriteInfo("");
            this.WriteInfo("Getting started with `help`");
            this.WriteInfo("");
        }

        public void WritePrompt(string text)
        {
            this.Write(ConsoleColor.White, text);
        }

        public void WriteInfo(string text)
        {
            this.WriteLine(ConsoleColor.Gray, text);
        }

        public void WriteError(Exception ex)
        {
            this.WriteLine(ConsoleColor.Red, ex.Message);

            if (ex is LiteException && (ex as LiteException).ErrorCode == LiteException.UNEXPECTED_TOKEN)
            {
                var err = ex as LiteException;

                this.WriteLine(ConsoleColor.DarkYellow, "> " + "^".PadLeft((int)err.Position + 1, ' '));
            }
        }

        public void WriteResult(IBsonDataReader result, Env env)
        {
            var index = 0;
            var writer = new JsonWriter(Console.Out)
            {
                Pretty = this.Pretty,
                Indent = 2
            };

            foreach (var item in result.ToEnumerable())
            {
                if (env.Running == false) return;

                this.Write(ConsoleColor.Cyan, string.Format("[{0}]: ", ++index));

                if (this.Pretty) Console.WriteLine();

                Console.ForegroundColor = ConsoleColor.DarkCyan;

                writer.Serialize(item);

                Console.WriteLine();
            }
        }

        #region Print public methods

        public void Write(string text)
        {
            this.Write(Console.ForegroundColor, text);
        }

        public void WriteLine(string text)
        {
            this.WriteLine(Console.ForegroundColor, text);
        }

        public void WriteLine(ConsoleColor color, string text)
        {
            this.Write(color, text + Environment.NewLine);
        }

        public void Write(ConsoleColor color, string text)
        {
            Console.ForegroundColor = color;
            Console.Write(text);
        }

        #endregion
    }
}