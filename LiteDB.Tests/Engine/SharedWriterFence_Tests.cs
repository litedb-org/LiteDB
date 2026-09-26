#if NET8_0_OR_GREATER
using System;
using System.IO;
using FluentAssertions;
using LiteDB.Client.Shared;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class SharedWriterFence_Tests
    {
        [Fact]
        public void Harmless_open_changes_admission_generation_but_preserves_writer_prefix_fence()
        {
            WithPage(page =>
            {
                page.Opened(5);
                page.TryRead(out var before).Should().BeTrue();
                page.OpeningBegin();
                page.TryRead(out _).Should().BeFalse();
                page.CanResume(before).Should().BeTrue();
                page.OpeningEnd(6);
                page.TryRead(out var after).Should().BeTrue();
                after.SameStorage(before).Should().BeFalse();
                after.SameWriterStorage(before).Should().BeTrue();
            });
        }

        [Theory]
        [InlineData("reuse")]
        [InlineData("structural")]
        [InlineData("reset")]
        [InlineData("interrupted-open")]
        public void Every_storage_fence_rejects_detached_writer_metadata(string mutation)
        {
            WithPage(page =>
            {
                page.Opened(5);
                page.TryRead(out var before).Should().BeTrue();
                page.OpeningBegin();
                if (mutation == "reuse") page.SlotReused();
                else if (mutation == "structural") { page.StructuralBegin(); page.StructuralEnd(5); }
                else if (mutation == "reset") page.Committed(0);
                else page.OpeningBegin(); // Successor recovers a predecessor's odd generation.
                page.CanResume(before).Should().BeFalse();
                page.OpeningEnd(5);
                page.TryRead(out var after).Should().BeTrue();
                after.SameWriterStorage(before).Should().BeFalse();
            });
        }

        private static void WithPage(Action<SharedCoordinationPage> test)
        {
            var directory = Path.Combine(Path.GetTempPath(), "litedb-writer-fence-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                using (var page = SharedCoordinationPage.Open(Path.Combine(directory, "test.db"))) test(page);
            }
            finally { Directory.Delete(directory, true); }
        }
    }
}
#endif
