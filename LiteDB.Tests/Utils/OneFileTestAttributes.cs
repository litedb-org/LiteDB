#if NET8_0_OR_GREATER
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
    internal static class OneFileTestPlatform
    {
        internal static string Reason => RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            RuntimeInformation.ProcessArchitecture == Architecture.X64 ? null :
            "ABI-2 mapped prototype refuses platforms other than qualified Linux x64; native backend tests remain enabled.";
    }
}
#endif
