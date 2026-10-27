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
    public sealed class LinuxX64FactAttribute : FactAttribute
    {
        public LinuxX64FactAttribute() { Skip = OneFileTestPlatform.LinuxX64Reason; }
    }
    public sealed class LinuxX64TheoryAttribute : TheoryAttribute
    {
        public LinuxX64TheoryAttribute() { Skip = OneFileTestPlatform.LinuxX64Reason; }
    }
    internal static class OneFileTestPlatform
    {
        internal static string Reason => SharedMemoryAuthority.IsPlatformQualified ? null :
            "ABI-2 mapped Shared is refused on this OS/architecture; native backend tests remain enabled.";
        internal static string LinuxX64Reason =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && RuntimeInformation.ProcessArchitecture == Architecture.X64
                ? null : "This test exercises the Linux x64 futex ABI; platform-neutral wake coverage runs through Shared tests.";
    }
}
#endif
