using System;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Engine;

public class SharedWorkerDumpArguments_Tests
{
    [Theory]
    [InlineData(@"-accepteula -ma -r -at 5 6448 ""C:\Temp\dump-6448-0123456789abcdef0123456789abcdef.dmp""")]
    [InlineData(@"-accepteula -ma -r -at 5 6448 ""C:\Temp\directory -64 name\dump-6448-0123456789abcdef0123456789abcdef.dmp""")]
    [InlineData(@"-accepteula -ma -r -at 5 6448 ""/tmp/directory -64 name/dump-6448-0123456789abcdef0123456789abcdef.dmp""")]
    public void CaptureCommand_AllowsOptionTextInsideQuotedPath(string arguments)
    {
        SharedWorkerDumpArguments.AssertCaptureCommand(arguments, 6448);
    }

    [Theory]
    [InlineData(@"-accepteula -ma -r -at 5 -64 6448 ""C:\Temp\directory -64 name\dump-6448-0123456789abcdef0123456789abcdef.dmp""")]
    [InlineData(@"-accepteula -ma -r -at 5 6448 -64 ""C:\Temp\directory -64 name\dump-6448-0123456789abcdef0123456789abcdef.dmp""")]
    [InlineData(@"-accepteula -ma -r -at 5 6448 ""C:\Temp\directory -64 name\dump-6448-0123456789abcdef0123456789abcdef.dmp"" -64")]
    [InlineData(@"-64 -accepteula -ma -r -at 5 6448 ""C:\Temp\dump-6448-0123456789abcdef0123456789abcdef.dmp""")]
    [InlineData(@"-accepteula -ma -r -at 5 6449 ""C:\Temp\dump-6448-0123456789abcdef0123456789abcdef.dmp""")]
    public void CaptureCommand_RejectsExtraOptionsAndWrongTarget(string arguments)
    {
        Action assert = () => SharedWorkerDumpArguments.AssertCaptureCommand(arguments, 6448);
        assert.Should().Throw<Xunit.Sdk.XunitException>();
    }
}
