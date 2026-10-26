using System;
using System.IO;
using System.Reflection;
#if !NET5_0_OR_GREATER
using System.Runtime.Serialization;
#endif
using Xunit;

namespace LiteDB.Tests.Utils
{
    public class TempFile_Tests
    {
        /// <summary>
        /// A TempFile whose constructor threw (here: copying a missing original) is still finalized. Its
        /// finalizer must not throw: an exception on the finalizer thread ends the whole test process.
        /// </summary>
        [Fact]
        public void Finalizing_a_temp_file_whose_constructor_failed_does_not_throw()
        {
            var missing = Path.Combine(Path.GetTempPath(), "litedb-missing-" + Guid.NewGuid().ToString("n") + ".db");
            Assert.ThrowsAny<IOException>(() => new TempFile(missing));

            // The same state, finalized deterministically: an instance whose Filename was never assigned.
#if NET5_0_OR_GREATER
            var unconstructed = (TempFile)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(TempFile));
#else
            var unconstructed = (TempFile)FormatterServices.GetUninitializedObject(typeof(TempFile));
#endif
            GC.SuppressFinalize(unconstructed);
            RunFinalizerPath(unconstructed);

            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        [Fact]
        public void Finalizing_a_temp_file_that_is_still_open_does_not_throw()
        {
            var file = new TempFile();
            GC.SuppressFinalize(file);
            using (var held = new FileStream(file.Filename, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                // Windows refuses the delete while the stream is open; POSIX unlinks the file.
                RunFinalizerPath(file);
            }
            File.Delete(file.Filename);
        }

        [Fact]
        public void Dispose_deletes_the_file()
        {
            var file = new TempFile();
            File.WriteAllText(file.Filename, "x");

            file.Dispose();

            Assert.False(File.Exists(file.Filename));
        }

        private static void RunFinalizerPath(TempFile file)
        {
            var dispose = typeof(TempFile).GetMethod("Dispose", BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(bool) }, null);
            dispose.Invoke(file, new object[] { false });
        }
    }
}
