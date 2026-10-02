using System;
using System.Threading;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>The connection string rejects Shared wait values the engine cannot accept, when parsed.</summary>
    public class SharedWaitGuardsParsing_Tests
    {
        [Theory]
        [InlineData("shared writer timeout=3000000")]          // about 34.7 days of seconds
        [InlineData("shared writer timeout=30.00:00:00")]
        [InlineData("shared self wait grace=3000000")]
        [InlineData("shared self wait grace=24.20:31:23.6480000")]
        public void Out_of_range_wait_values_are_rejected_at_parse(string option)
        {
            var error = Assert.Throws<LiteException>(() => new ConnectionString("filename=x.db;connection=shared;" + option));
            Assert.Contains("Int32.MaxValue milliseconds", error.Message);
        }

        [Theory]
        [InlineData("shared writer timeout=infinite", -1)]
        [InlineData("shared writer timeout=-1", -1)]
        [InlineData("shared writer timeout=0", 0)]
        [InlineData("shared writer timeout=2147483", 2147483000)]
        [InlineData("shared writer timeout=24.20:31:23.6470000", int.MaxValue)]
        public void In_range_wait_values_parse_and_reach_the_engine(string option, long milliseconds)
        {
            var cs = new ConnectionString("filename=x.db;connection=shared;" + option);
            var expected = milliseconds < 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(milliseconds);
            Assert.Equal(expected, cs.SharedWriterTimeout);
            // The engine setting accepts exactly what the parser accepts.
            var settings = new LiteDB.Engine.EngineSettings { SharedWriterTimeout = cs.SharedWriterTimeout };
            Assert.Equal(expected, settings.SharedWriterTimeout);
        }
    }
}
