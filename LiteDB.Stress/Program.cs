using LiteDB;
using LiteDB.Engine;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LiteDB.Stress
{
    public class Program
    {
        static void Main(string[] args)
        {
            var filename = args.Length >= 1 ? args[0] : "";
            var duration = TimeSpanEx.Parse(args.Length >= 2 ? args[1] : "60s");

            var e = new TestExecution(filename, duration);

            e.Execute();

            Console.ReadKey();
        }
    }
}
