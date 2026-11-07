#if NET8_0_OR_GREATER
using System;
using System.Threading;
using FluentAssertions;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedOneFileDeferredRead_Tests
    {
        [OneFileTheory]
        [InlineData(false, false)] [InlineData(false, true)]
        [InlineData(true, false)] [InlineData(true, true)]
        public void Later_streamed_transform_disposal_waits_until_its_Read_returns(bool pinned, bool sibling)
        {
            using var files = new SharedOneFilePublicationFixture();
            SharedEngine engine = null;
            var armed = false; var calls = 0; var closed = 0;
            var settings = files.Settings;
            settings.ReadTransform = (_, value) =>
            {
                if (armed && ++calls == 2)
                {
                    engine.Dispose();
                    Volatile.Read(ref closed).Should().Be(0, "Dispose must defer teardown until this executing Read returns");
                }
                return value;
            };
            using (engine = new SharedEngine(settings))
            using (var peer = sibling ? new SharedEngine(settings) : null)
            {
                engine.PinIdleLimit = engine.PinHoldLimit = TimeSpan.FromMinutes(10);
                using var anchor = pinned ? engine.Query("docs", new Query()) : null;
                if (anchor != null) { anchor.Read().Should().BeTrue(); engine.Update("docs", new[] { Row(1, 0) }); }
                var pin = engine.Pin;
                engine.BeginTrans().Should().BeTrue(); engine.Update("docs", new[] { Row(1, 99) });
                engine.OwnershipStage = stage => { if (stage == "core-closed") Interlocked.Increment(ref closed); };
                armed = true;
                using (var reader = engine.Query("docs", new Query()))
                {
                    reader.Read().Should().BeTrue();
                    reader.Read().Should().BeTrue();
                }
                if (pin != null) pin.WaitReleased();
                calls.Should().Be(2); Volatile.Read(ref closed).Should().Be(1);
                files.WriteIsFree();
                armed = false;
                if (peer != null) { peer.BeginTrans().Should().BeTrue(); peer.Rollback().Should().BeTrue(); }
                anchor?.Dispose();
            }
            for (var repeat = 0; repeat < 2; repeat++) { using var cold = new LiteDatabase(files.Filename); Verify(cold, 0); }
        }
    }
}
#endif
