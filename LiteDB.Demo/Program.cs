﻿using LiteDB;
using LiteDB.Engine;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading.Tasks;

namespace LiteDB.Demo
{
    class Program
    {
        static void Main(string[] args)
        {
            var sw = new Stopwatch();

            TestEngine.Run(sw);

            sw.Stop();

            Console.WriteLine(sw.ElapsedMilliseconds);
            Console.ReadKey();

        }
    }

    public class TestEngine
    {
        static string PATH = @"D:\memory-file.db";
        static string PATH_LOG = @"D:\memory-file-log.db";

        static BsonDocument doc = new BsonDocument
        {
            ["_id"] = 1,
            ["name"] = "NoSQL Database",
            ["birthday"] = new DateTime(1977, 10, 30),
            ["phones"] = new BsonValue(new string[] { "000000", "12345678" }),
            //["large"] = new byte[500],
            ["active"] = true
        }; // 109b (with no-large field)

        public static void Run(Stopwatch sw)
        {
            File.Delete(PATH);
            File.Delete(PATH_LOG);

            sw.Start();

            using (var db = new LiteEngine(new EngineSettings { Filename = PATH, CheckpointOnShutdown = true }))
            {

                IEnumerable<BsonDocument> source()
                {
                    for (var i = 0; i < 1000000; i++)
                    {
                        doc["_id"] = i + 1;
                        yield return doc;
                    }
                }

                //db.CreateCollection("col1");

                db.Insert("col1", source(), BsonAutoId.Int32);

                //sw.Stop();

            }

        }

    }
}
