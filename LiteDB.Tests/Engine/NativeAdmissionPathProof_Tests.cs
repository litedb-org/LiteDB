using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class NativeAdmissionPathProof_Tests
    {
        [Fact]
        public async Task Racing_different_fingerprints_cannot_both_be_admitted()
        {
            using var file = new TempFile();
            NativeAdmission_Tests.Seed(file);
            using var claimsReady = new Barrier(2);
            var claims = new[] { new byte[32], Enumerable.Repeat((byte)1, 32).ToArray() }
                .Select(hash => Task.Run(() =>
                {
                    var native = new DatabaseFileLock(file, readOnly: true, create: false);
                    var claimsMade = 0;
                    SharedCoordinationFile.CreationStage = (path, stage) =>
                    {
                        if (stage == "mode-shared-lock" && ++claimsMade == 4)
                            claimsReady.SignalAndWait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                    };
                    try { DatabasePathLock.Claim(native, hash); return native; }
                    catch (IOException error)
                    {
                        native.Dispose();
                        error.Message.Should().Contain("different canonical path");
                        return null;
                    }
                    catch { native.Dispose(); throw; }
                    finally { SharedCoordinationFile.CreationStage = null; }
                })).ToArray();
            try
            {
                var admitted = await Task.WhenAll(claims);
                admitted.Count(x => x != null).Should().BeLessOrEqualTo(1,
                    "different path claims overlap before either caller can release its native handle");
            }
            finally
            {
                foreach (var claim in claims)
                    if (claim.Status == TaskStatus.RanToCompletion) (await claim)?.Dispose();
            }
            using var next = new DatabaseFileLock(file, readOnly: true, create: false);
            DatabasePathLock.Claim(next, new byte[32]);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Fingerprint_range_endpoints_allow_matching_owners_and_reject_every_different_domain(bool lastByte)
        {
            using var file = new TempFile();
            NativeAdmission_Tests.Seed(file);
            var original = File.ReadAllBytes(file);
            var hash = Enumerable.Repeat(lastByte ? (byte)255 : (byte)0, 32).ToArray();
            using (var owner = new DatabaseFileLock(file, readOnly: true, create: false))
            {
                DatabasePathLock.Claim(owner, hash);
                using (var peer = new DatabaseFileLock(file, readOnly: true, create: false))
                    DatabasePathLock.Claim(peer, hash);
                for (var domain = 0; domain < 4; domain++)
                {
                    // Distances 1, 2^32 and 2^59 also prove that native queries
                    // preserve the length's high word on every supported ABI.
                    foreach (var distanceByte in new[] { 0, 4, 7 })
                    {
                        var otherHash = (byte[])hash.Clone();
                        otherHash[domain * 8 + distanceByte] ^= distanceByte == 7 ? (byte)8 : (byte)1;
                        using var mismatch = new DatabaseFileLock(file, readOnly: true, create: false);
                        Action claim = () => DatabasePathLock.Claim(mismatch, otherHash);
                        claim.Should().Throw<IOException>().WithMessage("*different canonical path*");
                    }
                    using var probe = new DatabaseFileLock(file, readOnly: true, create: false);
                    var start = (domain + 1) * (1L << 60);
                    var point = lastByte ? start + (1L << 60) - 1 : start;
                    probe.Conflicts(point).Should().BeTrue("each fingerprint byte is held in the kernel");
                    probe.Conflicts(lastByte ? start : start + 1).Should().BeFalse("failed peers released their bytes");
                    probe.Conflicts(DatabaseFileLock.Admission).Should().BeFalse("empty probes must not extend to EOF");
                }
            }
            using (var next = new DatabaseFileLock(file, readOnly: true, create: false))
                DatabasePathLock.Claim(next, Enumerable.Repeat((byte)37, 32).ToArray());
            File.ReadAllBytes(file).Should().Equal(original);
            NativeAdmission_Tests.Verify(file);
        }
    }
}
