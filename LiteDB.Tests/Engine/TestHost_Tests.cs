using System;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using LiteDB.Engine;
using Xunit;
using Xunit.Abstractions;

namespace LiteDB.Tests.Engine
{
    public class TestHost_Tests
    {
        private readonly ITestOutputHelper _output;

        public TestHost_Tests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void RequestedRuntimeAndArchitecture_AreActuallyRunning()
        {
            _output.WriteLine($"Test host: {RuntimeInformation.FrameworkDescription}, {RuntimeInformation.ProcessArchitecture}");
            var runtime = Environment.GetEnvironmentVariable("LITEDB_EXPECTED_RUNTIME_MAJOR");
            var architecture = Environment.GetEnvironmentVariable("LITEDB_EXPECTED_ARCHITECTURE");
            if (!string.IsNullOrEmpty(runtime))
            {
                Assert.Equal(runtime, Environment.Version.Major.ToString(CultureInfo.InvariantCulture));
            }
            if (!string.IsNullOrEmpty(architecture))
            {
                Assert.Equal(architecture.ToUpperInvariant(), RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant());
            }
        }

        [Fact]
        public void LoadedLibrary_ContainsTheRequiredEngineTestHooks()
        {
            Assert.NotNull(typeof(EngineState).GetField("SimulateDataWriteFail", BindingFlags.Instance | BindingFlags.NonPublic));
        }
    }
}
