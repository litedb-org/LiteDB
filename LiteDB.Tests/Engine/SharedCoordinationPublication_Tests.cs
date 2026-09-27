#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using FluentAssertions;
using LiteDB.Client.Shared;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class SharedCoordinationPublication_Tests
    {
        [Fact]
        public void Append_commit_changes_only_the_visible_version()
        {
            WithPage((page, view) =>
            {
                page.Opened(7);
                page.TryRead(out var before).Should().BeTrue();
                var sequence = view.ReadInt64(8);
                page.Committed(8);
                page.TryRead(out var after).Should().BeTrue();
                after.Version.Should().Be(8);
                after.SameStorage(before).Should().BeTrue();
                view.ReadInt64(8).Should().Be(sequence,
                    "append publication must not write the structural sequence");
                page.Opened(8);
                view.ReadInt64(8).Should().Be(sequence);
            });
        }

        [Fact]
        public void Version_reset_still_invalidates_old_snapshots()
        {
            WithPage((page, view) =>
            {
                page.Opened(7);
                page.TryRead(out var before).Should().BeTrue();
                var sequence = view.ReadInt64(8);
                page.Committed(0);
                page.TryRead(out var after).Should().BeTrue();
                after.Version.Should().Be(0);
                after.Resets.Should().Be(before.Resets + 1);
                after.SameStorage(before).Should().BeFalse();
                view.ReadInt64(8).Should().Be(sequence + 2);
            });
        }

        [Fact]
        public void Protected_open_finishes_its_structural_scope_and_establishes_trust_once()
        {
            WithPage((page, view) =>
            {
                page.StructuralBegin();
                page.TryRead(out _).Should().BeFalse();
                var sequence = view.ReadInt64(8);
                page.Opened(7, endStructural: true);
                page.TryRead(out var after).Should().BeTrue();
                after.Version.Should().Be(7);
                after.Structural.Should().Be(2);
                view.ReadInt64(8).Should().Be(sequence + 2);
                page.StructuralBegin();
                page.TryRead(out _).Should().BeFalse();
                page.StructuralEnd(8);
                page.TryRead(out after).Should().BeTrue();
                after.Structural.Should().Be(4);
            });
        }

        [Fact]
        public void Interrupted_sequence_requires_recovery_even_for_an_unchanged_version()
        {
            WithPage((page, view) =>
            {
                page.Opened(7);
                page.TryRead(out var before).Should().BeTrue();
                view.Write(8, view.ReadInt64(8) + 1);
                page.TryRead(out _).Should().BeFalse();
                page.Committed(7);
                page.TryRead(out var after).Should().BeTrue();
                after.Identity.Should().NotBe(before.Identity);
                after.SameStorage(before).Should().BeFalse();
                after.Version.Should().Be(7);
                (view.ReadInt64(8) & 1).Should().Be(0);
            });
        }

        [Fact]
        public void Unchanged_commit_after_disposal_does_not_access_the_released_pointer()
        {
            WithPage((page, _) =>
            {
                page.Opened(7);
                page.Dispose();
                Action commit = () => page.Committed(7);
                commit.Should().Throw<ObjectDisposedException>();
            });
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Interrupted_publisher_requires_a_protected_open_before_readmission(bool tornSequence)
        {
            WithPage((page, view) =>
            {
                page.Opened(7);
                page.TryRead(out var before).Should().BeTrue();
                page.BeginOpenRecovery().Should().BeFalse();
                // A different process died after publishing a structural begin,
                // or part way through the seqlock publication itself.
                if (tornSequence) view.Write(8, view.ReadInt64(8) + 1);
                else view.Write(24, view.ReadInt64(24) | 1);
                page.BeginOpenRecovery().Should().BeTrue();
                page.TryRead(out _).Should().BeFalse();
                page.Opened(7, endStructural: true);
                page.TryRead(out var after).Should().BeTrue();
                after.SameStorage(before).Should().BeFalse();
                page.BeginOpenRecovery().Should().BeFalse();
            });
        }

        [Fact]
        public void Writer_hint_expires_without_changing_storage_authority()
        {
            WithPage((page, view) =>
            {
                page.Opened(7);
                page.TryRead(out var before).Should().BeTrue();
                var sequence = view.ReadInt64(8);
                page.RequestWriterTurn(1000);
                page.ShouldYieldToWriter(1000).Should().BeTrue();
                page.ShouldYieldToWriter(1099).Should().BeTrue();
                page.ShouldYieldToWriter(1100).Should().BeFalse("a dead requester needs no cleanup to release pressure");
                page.ShouldYieldToWriter(999).Should().BeFalse("out-of-window contents are only an ignored hint");
                page.TryRead(out var after).Should().BeTrue();
                after.Version.Should().Be(before.Version);
                after.SameStorage(before).Should().BeTrue();
                view.ReadInt64(8).Should().Be(sequence);
                page.Dispose();
                page.RequestWriterTurn(2000);
                page.ShouldYieldToWriter(2000).Should().BeFalse();
            });
        }

        private static void WithPage(Action<SharedCoordinationPage, MemoryMappedViewAccessor> test)
        {
            var directory = Path.Combine(Path.GetTempPath(), "litedb-publication-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var file = Path.Combine(directory, "test.db");
                using var page = SharedCoordinationPage.Open(file);
                using var content = new FileStream(SharedCoordinationPage.PagePath(file),
                    FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                using var mapping = MemoryMappedFile.CreateFromFile(content, null, 4096,
                    MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: true);
                using var view = mapping.CreateViewAccessor();
                test(page, view);
            }
            finally { Directory.Delete(directory, true); }
        }
    }
}
#endif
