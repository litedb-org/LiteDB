#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using FluentAssertions;
using LiteDB.Client.Shared;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class SharedCoordinationPage_Tests
    {
        [Fact]
        public void Forgotten_mapping_releases_pointer_and_participation_handle_on_collection()
        {
            WithFile(file =>
            {
                var weak = AbandonMapping(file);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                weak.IsAlive.Should().BeFalse();
                SharedCoordinationPage.TryRetire(file);
                File.Exists(SharedCoordinationPage.PagePath(file)).Should().BeFalse();
            });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference AbandonMapping(string file)
        {
            var page = SharedCoordinationPage.Open(file);
            page.Opened(0);
            return new WeakReference(page);
        }

        [Fact]
        public void Revocation_probe_distinguishes_missing_present_and_unresolvable_paths()
        {
            WithFile(file =>
            {
                SharedCoordinationRevocation.IsRevoked(file).Should().BeFalse();
                File.WriteAllBytes(file, new byte[] { 1 });
                SharedCoordinationRevocation.IsRevoked(file).Should().BeTrue();
                // A file used as a parent component is not a missing marker: resolving
                // the supposed marker failed, so admission must be refused.
                SharedCoordinationRevocation.IsRevoked(Path.Combine(file, "marker")).Should().BeTrue();
            });
        }

        [Fact]
        public void New_participant_requires_a_protected_open_and_observes_every_published_fence()
        {
            WithFile(file =>
            {
                using (var writer = SharedCoordinationPage.Open(file))
                using (var reader = SharedCoordinationPage.Open(file))
                {
                    reader.TryRead(out _).Should().BeFalse();
                    writer.Opened(3);
                    reader.TryRead(out _).Should().BeFalse();
                    reader.Opened(3);
                    reader.TryRead(out var first).Should().BeTrue();
                    writer.StructuralBegin();
                    reader.TryRead(out _).Should().BeFalse();
                    writer.StructuralBegin();
                    writer.StructuralEnd(4);
                    reader.TryRead(out _).Should().BeFalse();
                    writer.StructuralEnd(4);
                    reader.TryRead(out var changed).Should().BeTrue();
                    changed.Version.Should().Be(4);
                    changed.SameStorage(first).Should().BeFalse();
                    writer.SlotReused();
                    reader.TryRead(out var reused).Should().BeTrue();
                    reused.Reuse.Should().Be(changed.Reuse + 1);
                    reused.SameStorage(changed).Should().BeFalse();
                    writer.Committed(0);
                    reader.TryRead(out var reset).Should().BeTrue();
                    reset.Resets.Should().Be(reused.Resets + 1);
                }
            });
        }

        [Fact]
        public void Revocation_reaches_all_existing_mappings_and_cannot_retire_a_live_authority()
        {
            WithFile(file =>
            {
                using (var first = SharedCoordinationPage.Open(file))
                {
                    first.Opened(10);
                    using (var second = SharedCoordinationPage.Open(file))
                    {
                        second.Opened(10);
                        SharedCoordinationPage.Revoke(file);
                        first.TryRead(out _).Should().BeFalse();
                        second.TryRead(out _).Should().BeFalse();
                    }
                    SharedCoordinationPage.TryRetire(file);
                    File.Exists(SharedCoordinationPage.PagePath(file)).Should().BeTrue();
                    File.Exists(SharedCoordinationPage.DisabledPath(file)).Should().BeTrue();
                }
                SharedCoordinationPage.TryRetire(file);
                File.Exists(SharedCoordinationPage.PagePath(file)).Should().BeFalse();
                File.Exists(SharedCoordinationPage.DisabledPath(file)).Should().BeFalse();
            });
        }

        [Theory]
        [InlineData(0)]
        [InlineData(3)]
        [InlineData(4096)]
        public void Unknown_control_bytes_are_rejected_without_truncation_or_repair(int length)
        {
            WithFile(file =>
            {
                var bytes = new byte[length];
                var path = SharedCoordinationPage.PagePath(file);
                File.WriteAllBytes(path, bytes);
                Action open = () => SharedCoordinationPage.Open(file);
                open.Should().Throw<IOException>();
                SharedCoordinationPage.TryRetire(file);
                File.ReadAllBytes(path).Should().Equal(bytes);
            });
        }

        [Fact]
        public void Repeated_structural_publication_never_exposes_an_intermediate_version()
        {
            WithFile(file =>
            {
                using (var writer = SharedCoordinationPage.Open(file))
                using (var reader = SharedCoordinationPage.Open(file))
                using (var start = new ManualResetEventSlim())
                {
                    writer.Opened(0);
                    reader.Opened(0);
                    var done = 0;
                    Exception failure = null;
                    var thread = new Thread(() =>
                    {
                        try
                        {
                            start.Wait();
                            for (var i = 0; i < 10000; i++)
                            {
                                writer.StructuralBegin();
                                writer.Committed(1);
                                writer.StructuralEnd(0);
                            }
                        }
                        catch (Exception error) { failure = error; }
                        finally { Volatile.Write(ref done, 1); }
                    });
                    thread.Start();
                    start.Set();
                    try
                    {
                        while (Volatile.Read(ref done) == 0)
                            if (reader.TryRead(out var status)) status.Version.Should().Be(0);
                    }
                    finally { thread.Join(TimeSpan.FromSeconds(10)).Should().BeTrue(); }
                    failure.Should().BeNull();
                }
            });
        }

        private static void WithFile(Action<string> test)
        {
            var directory = Path.Combine(Path.GetTempPath(), "litedb-coordination-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try { test(Path.Combine(directory, "test.db")); }
            finally { Directory.Delete(directory, true); }
        }
    }
}
#endif
