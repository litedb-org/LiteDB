#if NET8_0_OR_GREATER
using LiteDB.Client.Shared;
using System.Runtime.InteropServices;
using Xunit;

namespace LiteDB.Tests
{
    // Qualification follows production capability. Native range tests remain
    // ordinary Facts and exercise their backend on every available platform.
    public sealed class OneFileFactAttribute : FactAttribute
    {
        public OneFileFactAttribute() { Skip = OneFileTestPlatform.Reason; }
    }
    public sealed class OneFileTheoryAttribute : TheoryAttribute
    {
        public OneFileTheoryAttribute() { Skip = OneFileTestPlatform.Reason; }
    }
    public sealed class LinuxFutexFactAttribute : FactAttribute
    {
        public LinuxFutexFactAttribute() { Skip = OneFileTestPlatform.LinuxFutexReason; }
    }
    public sealed class LinuxFutexTheoryAttribute : TheoryAttribute
    {
        public LinuxFutexTheoryAttribute() { Skip = OneFileTestPlatform.LinuxFutexReason; }
    }
    internal static class OneFileTestPlatform
    {
        internal static string Reason => SharedMemoryAuthority.IsPlatformQualified ? null :
            "ABI-2 mapped Shared is refused on this OS/architecture; native backend tests remain enabled.";
        internal static string LinuxFutexReason => RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            ? Reason : "This test exercises the Linux futex ABI; native wake backend tests remain enabled.";
    }
}
#endif
