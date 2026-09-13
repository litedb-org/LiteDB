using System;
using System.Diagnostics;
using System.Linq;
using LiteDB;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

internal static class Program
{
    private static int Main()
    {
        ReproConfigurationReporter.SendConfiguration(ReproHostClient.CreateDefault());
        try
        {
            var samples = new double[3];
            for (var round = 0; round < samples.Length; round++)
            {
                var prefix = "round" + round + "-";
                var watch = Stopwatch.StartNew();
                var query = Query.EQ("field", prefix + "seed");
                for (var i = 0; i < 100; i++) query = Query.Or(query, Query.EQ("field", prefix + i));
                watch.Stop();
                samples[round] = watch.Elapsed.TotalMilliseconds;
                // Different literals per sample prevent a warmed compiler cache from hiding work.
                foreach (var i in Enumerable.Range(-1, 103))
                {
                    var input = new BsonDocument { ["field"] = prefix + i };
                    if (query.ExecuteScalar(input).AsBoolean != (i >= 0 && i < 100)) throw new Exception("OR truth table mismatch");
                }
                if (!query.ExecuteScalar(new BsonDocument { ["field"] = prefix + "seed" }).AsBoolean)
                    throw new Exception("lost the original disjunct");
            }
            Array.Sort(samples);
            Console.WriteLine($"MEASUREMENT medianMilliseconds={samples[1]:F3}");
            if (samples[1] > 500)
            {
                Console.WriteLine("BUG_1644_CONFIRMED: constructing 100 OR clauses exceeds the generous 500ms budget");
                return 0;
            }
            Console.WriteLine("VERIFIED_1644: construction budget and every disjunct passed on this host");
            return 10;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 20; }
    }
}
