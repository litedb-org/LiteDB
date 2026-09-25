#if NET8_0_OR_GREATER
using System;
using System.IO;

namespace LiteDB.Client.Coordinated
{
    /// <summary>
    /// Tells a new coordinator whether its predecessor may have left a status page that clients
    /// still trust. A graceful stop marks its page "no coordinator" before anything else, so only
    /// a coordinator that died leaves a trusted page behind, possibly one this process cannot see
    /// (another temp location) or write. The file <c>&lt;database&gt;-coordinator</c> exists for
    /// the whole life of every coordinator: created before its page, deleted only after the page
    /// was marked. A successor that finds it therefore knows a coordinator died, whether or not the
    /// OS reported the election mutex abandoned (it does not when the dead process held the last
    /// handle of the named mutex).
    /// </summary>
    internal sealed class CoordinatorMarker : IDisposable
    {
        private readonly string _path;
        private readonly FileStream _file;
        private int _disposed;

        private CoordinatorMarker(string path, FileStream file)
        {
            _path = path;
            _file = file;
        }

        internal static string PathFor(string filename) => Path.GetFullPath(filename) + "-coordinator";

        /// <summary>
        /// Create (or take over) the marker; the caller owns the election mutex, so no live
        /// coordinator holds it. <paramref name="predecessorDied"/> is true when a marker was
        /// left behind, or when this could not be established: then the caller must assume a
        /// dead coordinator's page is trusted somewhere. Returns null when the marker cannot be
        /// held, in which case the caller must not publish a status page.
        /// </summary>
        internal static CoordinatorMarker TryHold(string filename, out bool predecessorDied)
        {
            var path = PathFor(filename);
            try
            {
                predecessorDied = File.Exists(path);
                // No DeleteOnClose: the file must survive process death to be found.
                var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return new CoordinatorMarker(path, file);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                predecessorDied = true;
                return null;
            }
        }

        /// <summary>Graceful stop, after the status page was marked "no coordinator".</summary>
        public void Dispose()
        {
            if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _file.Dispose();
            // If this fails, the next coordinator only waits unnecessarily.
            try { File.Delete(_path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>Test crash: release the handle and leave the file, as process death would.</summary>
        internal void Abandon()
        {
            if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _file.Dispose();
        }
    }
}
#endif
