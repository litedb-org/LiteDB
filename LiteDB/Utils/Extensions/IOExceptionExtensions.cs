using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using static LiteDB.Constants;

namespace LiteDB
{
    internal static class IOExceptionExtensions
    {
        private const int ERROR_SHARING_VIOLATION = 32;
        private const int ERROR_LOCK_VIOLATION = 33;
        private const int LINUX_EAGAIN = 11;

        /// <summary>
        /// Detect if exception is an Locked exception
        /// </summary>
        public static bool IsLocked(this IOException ex)
        {
            var errorCode = Marshal.GetHRForException(ex) & ((1 << 16) - 1);

            return 
                errorCode == ERROR_SHARING_VIOLATION ||
                errorCode == ERROR_LOCK_VIOLATION ||
                // Unix byte-range locks report the raw errno, not a Win32 HRESULT.
                (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && ex.HResult == LINUX_EAGAIN);
        }

        /// <summary>
        /// Wait current thread for N milliseconds if exception is about Locking
        /// </summary>
        public static void WaitIfLocked(this IOException ex, int timerInMilliseconds)
        {
            if (ex.IsLocked())
            {
                if (timerInMilliseconds > 0)
                {
                    System.Threading.Tasks.Task.Delay(timerInMilliseconds).Wait();
                }
            }
            else
            {
                ExceptionDispatchInfo.Capture(ex).Throw();
            }
        }
    }
}
