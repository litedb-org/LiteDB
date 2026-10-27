#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedWriterTenureWakeSafety_Tests
    {
        [LinuxX64Theory]
        [InlineData("sampled")] [InlineData("rechecked")] [InlineData("syscall")] [InlineData("returned")]
        public void Wake_at_every_sample_recheck_and_syscall_edge_is_observed_before_final_detach(string cut)
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            var map = engine.MappedReaderTable.Authority.Map;
            using var reached = new ManualResetEventSlim();
            using var resume = new ManualResetEventSlim();
            var once = 0;
            map.WaitObserved = (stage, value) =>
            {
                if (stage != cut || Interlocked.Exchange(ref once, 1) != 0) return;
                reached.Set(); resume.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            };
            try
            {
                engine.Update("docs", new[] { Row(1, 1) });
                reached.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                map.Wake();
                resume.Set();
                engine.Update("docs", new[] { Row(1, 2) }).Should().Be(1);
            }
            finally { resume.Set(); map.WaitObserved = null; }
            engine.Dispose(); files.WriteIsFree();
            using var cold = new LiteDatabase(files.Filename); Verify(cold, 2);
        }

        [LinuxX64Fact]
        public void Final_facade_disposal_wakes_a_holder_proven_inside_the_shared_futex_syscall()
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            var table = engine.MappedReaderTable;
            var map = table.Authority.Map;
            var tid = 0;
            map.WaitObserved = (stage, _) => { if (stage == "syscall") Volatile.Write(ref tid, gettid()); };
            engine.Update("docs", new[] { Row(1, 1) });
            var pin = engine.Pin;
            SpinWait.SpinUntil(() =>
            {
                var nativeThread = Volatile.Read(ref tid);
                if (nativeThread == 0) return false;
                var call = File.ReadAllText("/proc/self/task/" + nativeThread + "/syscall");
                var fields = call.Split(' ');
                return fields.Length > 2 && fields[0] == "202" && fields[1] == "0x" + map.WakeAddress.ToInt64().ToString("x") && fields[2] == "0x0";
            }, TimeSpan.FromSeconds(5)).Should().BeTrue("the holder is observed in Linux x64 futex(202), not merely about to call it");
            engine.Dispose();
            table.Closed.Should().BeTrue();
            pin.RequestRelease(true); pin.WaitReleased();
            files.WriteIsFree();
            using var cold = new LiteDatabase(files.Filename); Verify(cold, 1);
        }

        [LinuxX64Fact]
        public void Wait_bounds_follow_pressure_and_EINTR_and_spurious_wakes_recheck_without_losing_the_holder()
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            var table = engine.MappedReaderTable;
            var map = table.Authority.Map;
            using var alone = new ManualResetEventSlim();
            using var pressured = new ManualResetEventSlim();
            var interrupted = 0;
            map.WaitObserved = (stage, value) =>
            {
                if (stage != "rechecked") return;
                if (value == 20) alone.Set();
                if (value == 1) pressured.Set();
            };
            map.SimulateWaitError = error => Interlocked.Increment(ref interrupted) <= 3 ? 4 : error;
            try
            {
                engine.BeginTrans().Should().BeTrue();
                var pin = engine.Pin;
                alone.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                using (var request = table.RequestWriter())
                {
                    pressured.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                    for (var n = 0; n < 8; n++) map.Wake();
                    engine.Update("docs", new[] { Row(1, 1) }).Should().Be(1);
                    engine.Pin.Should().BeSameAs(pin);
                    SpinWait.SpinUntil(() => Volatile.Read(ref interrupted) > 3, TimeSpan.FromSeconds(5)).Should().BeTrue();
                    Volatile.Read(ref interrupted).Should().BeGreaterThan(3);
                    engine.Rollback().Should().BeTrue();
                }
            }
            finally { map.WaitObserved = null; map.SimulateWaitError = null; }
            engine.Dispose(); files.WriteIsFree();
            using var cold = new LiteDatabase(files.Filename); Verify(cold, 0);
        }

        [LinuxX64Fact]
        public void Unexpected_wait_error_drains_a_live_operation_before_releasing_storage()
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            var map = engine.MappedReaderTable.Authority.Map;
            var errors = 0;
            engine.Update("docs", new[] { Row(1, 0) });
            var pin = engine.Pin;
            Exception observed = null;
            System.Collections.Generic.IEnumerable<BsonDocument> Input()
            {
                map.SimulateWaitError = _ => { Interlocked.Increment(ref errors); return 5; };
                map.Wake();
                SpinWait.SpinUntil(() => Volatile.Read(ref errors) > 0, TimeSpan.FromSeconds(5)).Should().BeTrue();
                pin.IsOperatingOn(Thread.CurrentThread).Should().BeTrue();
                observed = Record.Exception(() => files.WriteIsFree());
                yield return Row(1000, 1);
            }
            try { engine.Insert("admitted", Input(), BsonAutoId.Int32).Should().Be(1); }
            finally { map.SimulateWaitError = null; }
            observed.Should().BeOfType<IOException>("WRITE remains exclusive while the operation is draining");
            ((Action)pin.WaitReleased).Should().Throw<IOException>().WithMessage("*futex wait failed*5*");
            files.WriteIsFree();
            engine.Dispose();
            using var cold = new LiteDatabase(files.Filename); Verify(cold, 0);
            cold.GetCollection("admitted").FindAll().Should().BeEquivalentTo(new[] { Row(1000, 1) });
        }

        [LinuxX64Fact]
        public void Low_32_bit_wake_sequence_wrap_still_refuses_the_pre_wrap_wait_word()
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            var map = engine.MappedReaderTable.Authority.Map;
            map.Store(SharedMemoryLayout.WriterWake, uint.MaxValue);
            var before = map.Load(SharedMemoryLayout.WriterWake);
            map.Wake();
            map.Load(SharedMemoryLayout.WriterWake).Should().Be(1L << 32);
            var error = -1;
            map.WaitObserved = (stage, value) => { if (stage == "returned") error = value; };
            try { map.WaitWake(before, 900); }
            finally { map.WaitObserved = null; }
            error.Should().Be(11, "the kernel compares the low word and returns EAGAIN across wrap");
        }

        [DllImport("libc")] private static extern int gettid();
    }
}
#endif
