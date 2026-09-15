using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using LiteDB;

namespace Issue2794
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                var mode = args[0];
                var filename = args[1];
                Console.WriteLine("WORKER mode=" + mode + ", pid=" + Process.GetCurrentProcess().Id +
                    ", framework=" + AppDomain.CurrentDomain.SetupInformation.TargetFrameworkName +
                    ", litedb=" + typeof(LiteDatabase).Assembly.GetName().Version);
                using (var db = new LiteDatabase(new ConnectionString
                {
                    Filename = filename,
                    Connection = ConnectionType.Shared,
                    ReadOnly = mode == "verify"
                }))
                {
                    var jobs = db.GetCollection<Job>("jobs");
                    if (mode == "seed")
                    {
                        jobs.Insert(Enumerable.Range(1, JobLedger.Rows).Select(id => JobLedger.Expected(id, 0, false)));
                        JobLedger.VerifyAll(jobs, 0);
                        JobLedger.VerifyRawDates(db.GetCollection("jobs"), 0);
                        db.Checkpoint();
                    }
                    else if (mode == "verify")
                    {
                        JobLedger.VerifyAll(jobs, JobLedger.Rounds);
                        JobLedger.VerifyRawDates(db.GetCollection("jobs"), JobLedger.Rounds);
                    }
                    else if (mode == "client" || mode == "service")
                    {
                        File.WriteAllText(filename + "." + mode + ".ready", "ready");
                        WaitUntil(() => File.Exists(filename + ".go"));
                        if (mode == "client") RunClient(db, jobs);
                        else RunService(jobs);
                    }
                    else throw new ArgumentException("Unknown worker mode.");
                }
                Console.WriteLine("VERIFIED_WORKER_2794 mode=" + mode + ", rows=" + JobLedger.Rows +
                    ", rounds=" + JobLedger.Rounds);
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 20;
            }
        }

        private static void RunClient(LiteDatabase db, ILiteCollection<Job> jobs)
        {
            for (var round = 1; round <= JobLedger.Rounds; round++)
            {
                for (var id = 1; id <= JobLedger.Rows; id++)
                {
                    var key = JobLedger.Key(id);
                    JobLedger.Verify(jobs.FindById(key), JobLedger.Expected(id, round - 1, round > 1));
                    JobLedger.Require(jobs.Update(JobLedger.Expected(id, round, false)), "Client update was not acknowledged.");
                }
                WaitUntil(() =>
                {
                    var complete = true;
                    for (var id = 1; id <= JobLedger.Rows; id++)
                    {
                        var job = jobs.FindById(JobLedger.Key(id));
                        JobLedger.Require(job != null && job.Round == round, "Client observed a missing or stale round.");
                        var processed = job.Status == "deleted";
                        JobLedger.Verify(job, JobLedger.Expected(id, round, processed));
                        complete &= processed;
                    }
                    return complete;
                });
                // The service cannot advance without the client's next handoff.
                JobLedger.VerifyAll(jobs, round);
                if (round % 4 == 0) db.Checkpoint();
            }
        }

        private static void RunService(ILiteCollection<Job> jobs)
        {
            for (var round = 1; round <= JobLedger.Rounds; round++)
            {
                for (var id = 1; id <= JobLedger.Rows; id++)
                {
                    var key = JobLedger.Key(id);
                    WaitUntil(() =>
                    {
                        var job = jobs.FindById(key);
                        JobLedger.Require(job != null, "Service observed a missing job.");
                        if (job.Round == round - 1)
                        {
                            JobLedger.Verify(job, JobLedger.Expected(id, round - 1, round > 1));
                            return false;
                        }
                        JobLedger.Verify(job, JobLedger.Expected(id, round, false));
                        return true;
                    });
                    JobLedger.Verify(jobs.FindById(key), JobLedger.Expected(id, round, false));
                    JobLedger.Require(jobs.Update(JobLedger.Expected(id, round, true)), "Service update was not acknowledged.");
                }
            }
        }

        private static void WaitUntil(Func<bool> condition)
        {
            var deadline = Stopwatch.StartNew();
            while (!condition())
            {
                if (deadline.Elapsed > TimeSpan.FromSeconds(60)) throw new TimeoutException("Job handoff did not finish.");
                Thread.Sleep(1);
            }
        }
    }
}
