using System;
using System.Diagnostics;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2909_Tests
    {
#if NETFRAMEWORK
        [Fact(Skip = "The report targets net8.0 and net10.0; .NET Framework retains the netstandard fallback contract.")]
#else
        [Fact]
#endif
        public void ObjectId_uses_the_current_process_and_machine_on_NETCoreApp()
        {
            var id = ObjectId.NewObjectId();
            var expectedMachine = (Environment.MachineName.GetHashCode() +
                AppDomain.CurrentDomain.Id) & 0x00ffffff;

            id.Pid.Should().Be(unchecked((short)Process.GetCurrentProcess().Id));
            id.Machine.Should().Be(expectedMachine);
        }
    }
}
