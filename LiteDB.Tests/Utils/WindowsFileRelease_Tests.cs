using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace LiteDB.Tests.Utils
{
    public class WindowsFileRelease_Tests
    {
        [Theory]
        [InlineData(unchecked((int)0x80070020), true)]
        [InlineData(unchecked((int)0x80070021), true)]
        [InlineData(unchecked((int)0x80070005), false)]
        [InlineData(unchecked((int)0x80070070), false)]
        [InlineData(32, false)]
        public void Only_Windows_sharing_and_lock_violations_are_eligible(int hresult, bool expected)
        {
            Assert.Equal(expected, WindowsFileRelease.IsSharingViolation(new IOException("failure", hresult)));
        }

        [Fact]
        public void Other_IO_failures_are_not_retried()
        {
            var expected = new IOException("disk full", unchecked((int)0x80070070));
            var attempts = 0;
            var actual = Assert.Throws<IOException>(() => WindowsFileRelease.Retry(() =>
            {
                attempts++;
                throw expected;
            }));
            Assert.Same(expected, actual);
            Assert.Equal(1, attempts);
        }

        [Fact]
        public void Sharing_violations_are_not_retried_on_other_platforms()
        {
            if (WindowsFileRelease.IsWindows) return;
            var attempts = 0;
            Assert.Throws<IOException>(() => WindowsFileRelease.Retry(() =>
            {
                attempts++;
                throw new IOException("sharing", unchecked((int)0x80070020));
            }));
            Assert.Equal(1, attempts);
        }

        [Fact]
        public void Persistent_lock_still_throws_the_original_exception()
        {
            if (!WindowsFileRelease.IsWindows) return;
            using var file = new TempFile();
            using var held = new FileStream(file.Filename, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            IOException first = null;
            var attempts = 0;
            var elapsed = Stopwatch.StartNew();
            var actual = Assert.Throws<IOException>(() => WindowsFileRelease.Retry(() =>
            {
                attempts++;
                try { File.Delete(file.Filename); }
                catch (IOException error)
                {
                    if (first == null) first = error;
                    throw;
                }
            }, timeoutMilliseconds: 100));
            Assert.Same(first, actual);
            Assert.Contains(file.Filename, actual.Message);
            Assert.True(attempts > 1);
            Assert.True(elapsed.ElapsedMilliseconds >= 100);
            Assert.True(File.Exists(file.Filename));
        }

        [Fact]
        public async Task Lock_released_after_a_failed_attempt_allows_cleanup()
        {
            if (!WindowsFileRelease.IsWindows) return;
            using var file = new TempFile();
            using var held = new FileStream(file.Filename, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            using var failed = new ManualResetEventSlim();
            var release = Task.Run(() =>
            {
                Assert.True(failed.Wait(TimeSpan.FromSeconds(5)));
                held.Dispose();
            });
            var attempts = 0;
            try
            {
                WindowsFileRelease.Retry(() =>
                {
                    attempts++;
                    try { File.Delete(file.Filename); }
                    catch (IOException)
                    {
                        failed.Set();
                        throw;
                    }
                });
            }
            finally
            {
                failed.Set();
                await release;
            }
            Assert.True(attempts > 1);
            Assert.False(File.Exists(file.Filename));
        }

        [Fact]
        public void Exclusive_probe_preserves_contents_and_accepts_missing_files()
        {
            using var file = new TempFile();
            File.WriteAllText(file.Filename, "crash evidence");
            WindowsFileRelease.WaitForFiles(new[] { file.Filename, file.Filename + ".missing" });
            Assert.Equal("crash evidence", File.ReadAllText(file.Filename));
            Assert.False(File.Exists(file.Filename + ".missing"));
        }

        [Fact]
        public void Exclusive_probe_does_not_ignore_a_persistent_lock()
        {
            if (!WindowsFileRelease.IsWindows) return;
            using var file = new TempFile();
            using var held = new FileStream(file.Filename, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            Assert.Throws<IOException>(() => WindowsFileRelease.WaitForFiles(new[] { file.Filename }, 100));
        }

        [Fact]
        public void Directory_cleanup_does_not_hide_nonsharing_errors()
        {
            var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Assert.Throws<DirectoryNotFoundException>(() => WindowsFileRelease.DeleteDirectory(missing));
        }

        [Fact]
        public async Task Directory_cleanup_waits_for_a_locked_nested_file()
        {
            if (!WindowsFileRelease.IsWindows) return;
            var root = Path.Combine(Path.GetTempPath(), "litedb-release-" + Guid.NewGuid().ToString("N"));
            var nested = Directory.CreateDirectory(Path.Combine(root, "readers")).FullName;
            var path = Path.Combine(nested, "slots.lease");
            using var held = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            Assert.Throws<IOException>(() => Directory.Delete(root, true));
            using var started = new ManualResetEventSlim();
            var cleanup = Task.Run(() =>
            {
                started.Set();
                WindowsFileRelease.DeleteDirectory(root);
            });
            try
            {
                Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
                Assert.NotSame(cleanup, await Task.WhenAny(cleanup, Task.Delay(100)));
                Assert.True(File.Exists(path));
            }
            finally
            {
                held.Dispose();
                await cleanup;
            }
            Assert.False(Directory.Exists(root));
        }
    }
}
