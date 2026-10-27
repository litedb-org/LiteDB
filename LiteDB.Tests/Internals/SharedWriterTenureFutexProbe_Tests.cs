#if NET8_0_OR_GREATER
using System;
using System.Threading;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedWriterTenureFutexProbe_Tests
    {
        [LinuxFutexFact]
        public void A_wake_before_wait_prevents_sleeping_on_the_old_word()
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            var map = engine.MappedReaderTable.Authority.Map;
            var observed = map.Load(SharedMemoryLayout.WriterWake);
            map.Wake();
            Exception error = null;
            var sleeper = new Thread(() => error = Record.Exception(() => map.WaitWake(observed, 900))) { IsBackground = true };
            sleeper.Start();
            var prompt = sleeper.Join(TimeSpan.FromMilliseconds(500));
            if (!prompt) { map.Wake(); sleeper.Join(TimeSpan.FromSeconds(3)).Should().BeTrue(); }
            error.Should().BeNull();
            prompt.Should().BeTrue("FUTEX_WAIT must observe the published word and return EAGAIN, without using its 900 ms timeout");
        }

        [LinuxFutexFact]
        public void A_spurious_wake_and_stale_pin_release_preserve_mapping_lifetime()
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            engine.Update("docs", new[] { Row(1, 1) });
            var table = engine.MappedReaderTable;
            var pin = engine.Pin;
            table.Authority.Map.Wake();
            engine.Update("docs", new[] { Row(1, 2) });
            engine.Pin.Should().BeSameAs(pin);
            engine.Dispose();
            table.Closed.Should().BeTrue();
            pin.RequestRelease(force: true);
            pin.WaitReleased();
            files.WriteIsFree();
            using var cold = new LiteDatabase(files.Filename); Verify(cold, 2);
        }
    }
}
#endif
