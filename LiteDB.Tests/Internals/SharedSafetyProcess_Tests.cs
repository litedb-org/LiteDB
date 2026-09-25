#if !NETFRAMEWORK
using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace LiteDB.Internals
{
    public class SharedSafetyProcess_Tests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedb-shared-safety-" + Guid.NewGuid().ToString("N"));
        private string Filename => Path.Combine(_directory, "test.db");

        public SharedSafetyProcess_Tests() => Directory.CreateDirectory(_directory);

        [Theory]
        [InlineData(null, "before")]
        [InlineData(null, "after")]
        [InlineData("secret", "before")]
        [InlineData("secret", "after")]
        public async Task RelativeConnection_KeepsDatabaseAndLeaseIdentityAfterDirectoryChange(string password, string when)
        {
            var other = Path.Combine(_directory, "other", "test.db");
            Directory.CreateDirectory(Path.GetDirectoryName(other));
            await MvccProcess.Run("seed", Filename, password);
            await MvccProcess.Run("seed", other, password);
            await MvccProcess.Run("write", other, password, "7");
            await MvccProcess.Run("shared-relative", Filename, password, when);
            using (var original = new MvccProcess("read", Filename, password))
            {
                await original.Expect("value:1");
                await original.Finish();
            }
            using (var unrelated = new MvccProcess("read", other, password))
            {
                await unrelated.Expect("value:7");
                await unrelated.Finish();
            }
        }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
#endif
