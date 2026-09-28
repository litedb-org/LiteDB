using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class NativeAdmissionOwnershipProof_Tests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task Last_release_overlapping_reference_publication_preserves_exact_native_ownership(
            bool shared, bool failPublication)
        {
            using var file = new TempFile();
            NativeAdmission_Tests.Seed(file);
            using var publishing = new ManualResetEventSlim();
            using var releaseStarted = new ManualResetEventSlim();
            using var finishPublication = new ManualResetEventSlim();
            using var original = SharedModeGuard.Open(file, shared, SharedMutexNameStrategy.Default);
            SharedModeGuard retained = null;
            var publicationCount = 0;
            var acquisition = Task.Run(() =>
            {
                SharedCoordinationFile.CreationStage = (path, stage) =>
                {
                    if (stage != "mode-retaining") return;
                    Interlocked.Increment(ref publicationCount);
                    publishing.Set();
                    finishPublication.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                    if (failPublication) throw new IOException("proof: failed reference publication");
                };
                try
                {
                    Action acquire = () => retained = SharedModeGuard.Open(file, shared, SharedMutexNameStrategy.Default);
                    if (failPublication) acquire.Should().Throw<DatabaseAdmissionException>()
                        .WithMessage("*proof: failed reference publication*");
                    else acquire();
                }
                finally { SharedCoordinationFile.CreationStage = null; }
            });
            Task release = null;
            try
            {
                publishing.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                release = Task.Run(() => { releaseStarted.Set(); original.Dispose(); });
                releaseStarted.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                // Retain holds the registry gate while the original's disposal
                // contends for it. A raw descriptor cannot be fooled by that gate.
                IsLocked(file).Should().BeTrue("publication and the original release overlap");
                finishPublication.Set();
                await Task.WhenAll(acquisition, release);
                publicationCount.Should().Be(1);
                IsLocked(file).Should().Be(!failPublication, "only a published reference may retain admission");
                retained?.Dispose();
                retained?.Dispose();
                IsLocked(file).Should().BeFalse("the final reference was released exactly once");
                NativeAdmission_Tests.Verify(file);
            }
            finally
            {
                finishPublication.Set();
                await acquisition;
                if (release != null) await release;
                retained?.Dispose();
            }
        }

        private static bool IsLocked(string path)
        {
            using var probe = new DatabaseFileLock(path, readOnly: true, create: false);
            return probe.Conflicts(DatabaseFileLock.Admission);
        }
    }
}
