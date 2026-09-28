using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class NativeAdmissionFinalizer_Tests
    {
        private sealed class Result
        {
            internal int Flushed;
            internal Exception Error;
        }

        // FileStream finalizers may flush buffered writes. This ordinary finalizer
        // models that last write and verifies that admission still excludes peers.
        private sealed class BufferedOwner
        {
            private readonly string _filename;
            private readonly Result _result;
            internal BufferedOwner(string filename, Result result) { _filename = filename; _result = result; }
            ~BufferedOwner()
            {
                try
                {
                    using var contender = SharedModeGuard.Open(_filename, true, SharedMutexNameStrategy.Default);
                    _result.Error = new Exception("Admission ended before the buffered owner's finalizer.");
                }
                catch (DatabaseAdmissionException) { Interlocked.Increment(ref _result.Flushed); }
                catch (Exception error) { _result.Error = error; }
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Abandoned_admission_outlives_ordinary_buffer_finalizers(bool reverseAllocation)
        {
            using var file = new TempFile();
            NativeAdmission_Tests.Seed(file);
            for (var i = 0; i < 8; i++)
            {
                var result = new Result();
                Forget(file, result, reverseAllocation);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                result.Error.Should().BeNull();
                result.Flushed.Should().Be(1);
                using (SharedModeGuard.Open(file, true, SharedMutexNameStrategy.Default)) { }
            }
            NativeAdmission_Tests.Verify(file);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Forget(string filename, Result result, bool reverse)
        {
            BufferedOwner owner = null;
            if (reverse) owner = new BufferedOwner(filename, result);
            var admission = SharedModeGuard.Open(filename, false, SharedMutexNameStrategy.Default);
            if (!reverse) owner = new BufferedOwner(filename, result);
            GC.KeepAlive(admission);
            GC.KeepAlive(owner);
        }
    }
}
