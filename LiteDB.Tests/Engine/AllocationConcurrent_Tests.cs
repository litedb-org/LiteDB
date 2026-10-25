using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class AllocationConcurrent_Tests
    {
        [Theory]
        [InlineData(null, 1)]
        [InlineData("secret", 1)]
        [InlineData(null, 1000)]
        [InlineData("secret", 1000)]
        public void Multiple_owners_commit_rollback_and_checkpoint_out_of_allocation_order(string password, int limit)
        {
            using var file = new TempFile();
            using (var db = AllocationWorkload.Open(file.Filename, password, limit))
            {
                AllocationWorkload.Seed(db, "hole");
                using var ready = new CountdownEvent(3);
                var release = Enumerable.Range(0, 3).Select(_ => new ManualResetEventSlim()).ToArray();
                var finished = Enumerable.Range(0, 3).Select(_ => new ManualResetEventSlim()).ToArray();
                var errors = new Exception[3];
                var threads = Enumerable.Range(0, 3).Select(index => new Thread(() =>
                {
                    try
                    {
                        db.BeginTrans();
                        db.GetCollection("owner" + index).Insert(Enumerable.Range(100, 12).Select(AllocationWorkload.Document));
                        ready.Signal();
                        if (!release[index].Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException();
                        if (index == 0) db.Commit();
                        if (index == 1) db.Rollback();
                        // index 2 exits idle, leaving its reservation abandoned.
                    }
                    catch (Exception ex) { errors[index] = ex; }
                    finally { finished[index].Set(); }
                }) { IsBackground = true }).ToArray();
                foreach (var thread in threads) thread.Start();
                try
                {
                    ready.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue();
                    db.GetCollection("b").Insert(Enumerable.Range(200, 8).Select(AllocationWorkload.Document));
                    db.Checkpoint(); // Active writers/safepoints must retain their private versions.
                    db.UserVersion = 3082;
                    foreach (var index in new[] { 1, 0, 2 })
                    {
                        release[index].Set();
                        finished[index].Wait(TimeSpan.FromSeconds(30)).Should().BeTrue();
                        db.UserVersion++;
                    }
                }
                finally
                {
                    foreach (var signal in release) signal.Set();
                    foreach (var thread in threads) thread.Join(TimeSpan.FromSeconds(30)).Should().BeTrue();
                    foreach (var signal in release.Concat(finished)) signal.Dispose();
                }
                errors.Should().OnlyContain(error => error == null);
            }
            using (var recovered = AllocationWorkload.Open(file.Filename, password))
            {
                recovered.GetCollection("owner0").FindAll().Should().BeEquivalentTo(Enumerable.Range(100, 12).Select(AllocationWorkload.Document));
                recovered.CollectionExists("owner1").Should().BeFalse();
                recovered.CollectionExists("owner2").Should().BeFalse();
                recovered.GetCollection("b").Count().Should().Be(9);
                recovered.Checkpoint();
            }
            AllocationReachability.Verify(file.Filename, password);
        }
    }
}
