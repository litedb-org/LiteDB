using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Xml;

namespace LiteDB.Stress
{
    public class ThreadInfo
    {
        public ITestItem Task { get; set; }
        public int Counter { get; set; } = 0;
        public bool Running { get; set; } = false;
        public Stopwatch Elapsed { get; } = new Stopwatch();
        public DateTime LastRun { get; set; } = DateTime.Now;
        public BsonValue Result { get; set; } = null;
        public long ResultSum { get; set; }
        public TimeSpan TotalRun { get; set; } = TimeSpan.Zero;
        public Exception Exception { get; set; }
        public Thread Thread { get; set; }
        public CancellationTokenSource CancellationTokenSource { get; } = new CancellationTokenSource();
    }
}
