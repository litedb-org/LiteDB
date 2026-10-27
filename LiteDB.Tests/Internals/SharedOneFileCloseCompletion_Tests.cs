#if NET8_0_OR_GREATER
using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedOneFileCloseCompletion_Tests
    {
        [OneFileTheory]
        [InlineData(null, "exclusive")]
        [InlineData("secret", "exclusive")]
        [InlineData(null, "slot-cleared")]
        [InlineData("secret", "slot-cleared")]
        [InlineData(null, "dms-released")]
        [InlineData("secret", "dms-released")]
        public async Task Closed_requires_completed_final_storage_and_native_release(string password, string cut)
        {
            using var files = new SharedOneFilePublicationFixture(password);
            using var engine = new SharedEngine(files.Settings);
            var table = engine.MappedReaderTable;
            using var reached = new ManualResetEventSlim();
            using var resume = new ManualResetEventSlim();
            SharedMemoryFinalClose.FinalCloseStage = stage =>
            {
                if (stage != cut) return;
                reached.Set();
                if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Final close was not resumed.");
            };
            var close = Task.Run(engine.Dispose);
            try
            {
                reached.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                table.HasReaders.Should().BeFalse();
                close.IsCompleted.Should().BeFalse();
                table.Closed.Should().BeFalse("zero references do not mean final storage and native cleanup have finished");
            }
            finally
            {
                resume.Set();
                try { await close; }
                finally { SharedMemoryFinalClose.FinalCloseStage = null; }
            }
            table.Closed.Should().BeTrue();
            using var cold = new LiteDatabase(new ConnectionString { Filename = files.Filename, Password = password });
            Verify(cold, 0);
        }
    }
}
#endif
