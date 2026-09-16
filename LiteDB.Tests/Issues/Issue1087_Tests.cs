using System;
using System.IO;
using System.Runtime.InteropServices;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1087_Tests
    {
        [Fact]
        public void Native_errno_11_is_a_lock_collision_only_on_Linux()
        {
            new IOException("native contention", 11).IsLocked()
                .Should().Be(RuntimeInformation.IsOSPlatform(OSPlatform.Linux));
            new IOException("Win32 bad format", unchecked((int)0x8007000b)).IsLocked().Should().BeFalse();
        }

        [Theory]
        [InlineData(32)]
        [InlineData(33)]
        [InlineData(unchecked((int)0x80070020))]
        [InlineData(unchecked((int)0x80070021))]
        public void Existing_sharing_and_lock_errors_remain_retryable(int code)
        {
            new IOException("lock", code).IsLocked().Should().BeTrue();
        }

        [Theory]
        [InlineData(2)]
        [InlineData(5)]
        [InlineData(28)]
        [InlineData(112)]
        public void Unrelated_io_errors_escape_both_helpers_without_retries(int code)
        {
            var error = new IOException("non-lock sentinel", code);
            var attempts = 0;
            Action operation = () => { attempts++; throw error; };
            Action run = () => FileHelper.Exec(1, operation);
            run.Should().Throw<IOException>().Which.Should().BeSameAs(error);
            attempts.Should().Be(1);
            attempts = 0;
            run = () => FileHelper.TryExec(1, operation);
            run.Should().Throw<IOException>().Which.Should().BeSameAs(error);
            attempts.Should().Be(1);
        }

        [Fact]
        public void Linux_retry_helpers_reach_success_and_respect_an_expired_timeout()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return;
            var error = new IOException("native contention", 11);
            var attempts = 0;
            Action operation = () => { if (++attempts < 3) throw error; };
            FileHelper.Exec(1, operation);
            attempts.Should().Be(3);
            attempts = 0;
            FileHelper.TryExec(1, operation).Should().BeTrue();
            attempts.Should().Be(3);

            operation = () => { attempts++; throw error; };
            attempts = 0;
            FileHelper.TryExec(0, operation).Should().BeFalse();
            attempts.Should().Be(1);
            attempts = 0;
            Action run = () => FileHelper.Exec(0, operation);
            run.Should().Throw<IOException>().Which.Should().BeSameAs(error);
            attempts.Should().Be(1);
        }
    }
}
