using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class AllocationPublication_Tests
    {
        public static IEnumerable<object[]> Cases()
        {
            foreach (var password in new[] { null, "secret" })
            foreach (var mode in new[] { "hole", "tail", "free" })
            foreach (var limit in new[] { 1, 1000 })
            foreach (var completion in new[] { "abandon", "commit", "rollback" })
                yield return new object[] { password, mode, limit, completion };
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void Abandoned_allocations_leave_a_complete_reusable_page_graph(string password, string mode, int limit, string completion)
        {
            using var file = new TempFile();
            using (var db = AllocationWorkload.Open(file.Filename, password, limit))
            {
                AllocationWorkload.Seed(db, mode);
                AllocationWorkload.Publish(db, mode, completion);
            }
            uint last = 0;
            for (var retry = 0; retry < 2; retry++)
            {
                using var db = AllocationWorkload.Open(file.Filename, password);
                AllocationWorkload.Verify(db, completion == "commit");
                db.Checkpoint();
                if (retry == 0) last = AllocationFileGraph.Verify(file.Filename, password);
            }
            AllocationFileGraph.Verify(file.Filename, password).Should().Be(last);
            AllocationReachability.Verify(file.Filename, password);
            if (completion != "commit")
            {
                using (var db = AllocationWorkload.Open(file.Filename, password))
                {
                    db.GetCollection("a").Insert(Enumerable.Range(100, 12).Select(AllocationWorkload.Document));
                    AllocationWorkload.Verify(db, true);
                    db.Checkpoint();
                }
                AllocationFileGraph.Verify(file.Filename, password).Should().Be(last, "abandoned reservations must be reused");
            }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Repeated_header_publications_reuse_confirmed_reservation_images(string password)
        {
            using var file = new TempFile();
            using (var engine = new LiteDB.Engine.LiteEngine(new LiteDB.Engine.EngineSettings
            {
                Filename = file.Filename, Password = password, TransactionPageLimit = 1,
                CompactStorage = CompactStorageMode.Legacy
            }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                AllocationWorkload.Seed(db, "hole");
                var emptyWrites = 0;
                AllocationWorkload.Publish(db, "hole", stage: stage =>
                {
                    if (stage == "allocated")
                    {
                        engine.SimulateDiskWriteFail = page =>
                        {
                            if (page[4] == 0) emptyWrites++;
                        };
                    }
                    if (stage == "committed")
                    {
                        emptyWrites.Should().BeGreaterThan(0, "the first commit must make reservations durable");
                        emptyWrites = 0;
                    }
                    if (stage == "published")
                    {
                        for (var i = 0; i < 3; i++) db.UserVersion++;
                        emptyWrites.Should().Be(0, "unchanged reservations already have confirmed empty images");
                    }
                });
                engine.SimulateDiskWriteFail = null;
            }
            using (var db = AllocationWorkload.Open(file.Filename, password)) db.Checkpoint();
            AllocationReachability.Verify(file.Filename, password);
        }
    }
}
