using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace LiteDB.Tests.Internals
{
    public class BenchmarkStartSignal_Tests
    {
        [Fact]
        public void Published_signal_can_be_read_before_the_Windows_rename_handle_closes()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
            using var unpublished = new TempFile();
            using var signal = new TempFile();
            var expected = new DateTime(638950464001234567, DateTimeKind.Utc);
            File.WriteAllText(unpublished, expected.Ticks.ToString(CultureInfo.InvariantCulture));
            // Hold the DELETE access used during MoveFileEx past publication,
            // deterministically exposing the previously intermittent reader race.
            using var publisher = CreateFileW(unpublished, 0x10000, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
            publisher.IsInvalid.Should().BeFalse();
            File.Move(unpublished, signal);
            Action oldReader = () => File.ReadAllText(signal);
            oldReader.Should().Throw<IOException>();
            BenchmarkStartSignal.Read(signal).Should().Be(expected);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string filename, uint access, uint share,
            IntPtr security, uint creation, uint flags, IntPtr template);
    }
}
