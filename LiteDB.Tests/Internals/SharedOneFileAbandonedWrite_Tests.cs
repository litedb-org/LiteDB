#if NET8_0_OR_GREATER
using System;
using System.IO;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Tests;

namespace LiteDB.Internals
{
    public class SharedOneFileAbandonedWrite_Tests
    {
        [OneFileFact]
        public void An_abandoned_named_mutex_hint_never_steals_a_live_native_writer()
        {
            using var file = new MappedTestFile();
            using var owner = new SharedRangeLocks(file.Filename + "-shm");
            using var successor = new SharedRangeLocks(file.Filename + "-shm");
            owner.TryClaim(0).Should().BeTrue();
            owner.EnterWrite();
            Action mismatched = () => successor.EnterWrite(abandonedOwner: true);
            mismatched.Should().Throw<IOException>().WithMessage("*ownership mismatch*");
            successor.IsLive(0).Should().BeTrue();
            Action stillHeld = successor.EnterWrite;
            stillHeld.Should().Throw<IOException>().WithMessage("*ownership mismatch*");
            owner.ExitWrite();
            successor.EnterWrite(abandonedOwner: true);
            successor.ExitWrite();
        }
    }
}
#endif
