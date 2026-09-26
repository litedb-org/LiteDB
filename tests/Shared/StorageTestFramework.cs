using System.Reflection;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace LiteDB.Tests
{
    public sealed class StorageTestFramework : XunitTestFramework
    {
        public StorageTestFramework(IMessageSink messageSink) : base(messageSink)
        {
        }

        // xUnit falls back to its default framework if a constructor throws. Validate
        // here instead, so configuration errors cannot silently enable disk writes.
        protected override ITestFrameworkDiscoverer CreateDiscoverer(IAssemblyInfo assemblyInfo)
        {
            InitializeStorage();
            return base.CreateDiscoverer(assemblyInfo);
        }

        protected override ITestFrameworkExecutor CreateExecutor(AssemblyName assemblyName)
        {
            InitializeStorage();
            return base.CreateExecutor(assemblyName);
        }

        private void InitializeStorage() => DiagnosticMessageSink.OnMessage(
            new Xunit.Sdk.DiagnosticMessage("Test filesystem root: " + TestStorage.Root));
    }
}
