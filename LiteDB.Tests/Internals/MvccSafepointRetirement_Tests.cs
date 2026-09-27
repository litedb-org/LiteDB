using System;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using FluentAssertions;
using Xunit;

namespace LiteDB.Internals
{
    public class MvccSafepointRetirement_Tests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void NewHolesBetweenSafepoints_CannotMakeRecoverySelectAnOlderPageImage(string password)
        {
            using var test = new WalTestDatabase(password);
            test.Seed("history");
            test.Seed("pending");
            for (var value = 1; value <= 12; value++) test.Update("history", value);
            using var ready = new ManualResetEventSlim();
            using var reclaimDone = new ManualResetEventSlim();
            Exception failure = null;
            var writer = new Thread(() =>
            {
                try
                {
                    test.Database.BeginTrans();
                    test.Update("pending", 1);
                    var transaction = test.Engine.GetMonitor().GetThreadTransaction();
                    transaction.Safepoint();
                    ready.Set();
                    reclaimDone.Wait(TimeSpan.FromSeconds(20)).Should().BeTrue();
                    test.Update("pending", 2);
                    transaction.Safepoint();
                    test.Database.Commit();
                }
                catch (Exception error) { failure = error; ready.Set(); }
            }) { IsBackground = true };
            writer.Start();
            try
            {
                ready.Wait(TimeSpan.FromSeconds(20)).Should().BeTrue();
                if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
                test.Engine.Checkpoint();
            }
            finally
            {
                reclaimDone.Set();
                writer.Join(TimeSpan.FromSeconds(20)).Should().BeTrue();
            }
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
            foreach (var checkpoint in new[] { false, true })
            {
                var rows = test.Recover("pending", checkpoint).OrderBy(row => row["_id"].AsInt32).ToArray();
                rows.Should().HaveCount(WalTestDatabase.DocumentCount);
                for (var id = 0; id < rows.Length; id++)
                {
                    rows[id]["_id"].AsInt32.Should().Be(id);
                    rows[id]["value"].AsInt32.Should().Be(2);
                    rows[id]["payload"].AsString.Should().Be(new string('x', 1500));
                }
            }
        }
    }
}
