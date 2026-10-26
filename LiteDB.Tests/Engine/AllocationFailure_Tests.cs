using System;
using System.IO;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class AllocationFailure_Tests
    {
        [Theory]
        [InlineData(null, 1)]
        [InlineData("secret", 1)]
        [InlineData(null, 5)]
        [InlineData("secret", 5)]
        [InlineData(null, 0)]
        [InlineData("secret", 0)]
        public void Failed_reservation_or_confirmation_write_stops_the_engine_without_publishing_allocations(string password, int failAt)
        {
            using var file = new TempFile();
            using (var engine = new LiteEngine(new EngineSettings
            {
                Filename = file.Filename, Password = password, CompactStorage = CompactStorageMode.Legacy,
                TransactionPageLimit = 1
            }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                AllocationWorkload.Seed(db, "hole");
                var emptyWrites = 0;
                var failed = false;
                Action publish = () => AllocationWorkload.Publish(db, "hole", stage: stage =>
                {
                    if (stage != "allocated") return;
                    engine.SimulateDiskWriteFail = page =>
                    {
                        var empty = page[BasePage.P_PAGE_TYPE] == (byte)PageType.Empty;
                        var confirmation = page[BasePage.P_PAGE_TYPE] == (byte)PageType.Header && page.ReadBool(BasePage.P_IS_CONFIRMED);
                        if ((empty && ++emptyWrites == failAt) || (confirmation && failAt == 0))
                        {
                            failed = true;
                            throw new IOException("allocation publication failure");
                        }
                    };
                });
                publish.Should().Throw<IOException>().WithMessage("allocation publication failure");
                failed.Should().BeTrue("the injected write must be reached");
                Action write = () => db.GetCollection("b").Insert(AllocationWorkload.Document(999));
                write.Should().Throw<Exception>();
            }
            for (var retry = 0; retry < 2; retry++)
            {
                using var recovered = AllocationWorkload.Open(file.Filename, password);
                AllocationWorkload.Verify(recovered, committed: false, headerPublished: false);
                recovered.Checkpoint();
            }
            AllocationReachability.Verify(file.Filename, password);
        }
    }
}
