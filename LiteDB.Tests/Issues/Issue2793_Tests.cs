using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2793_Tests
    {
        [Fact]
        public void Direct_mode_control_can_write_and_independently_reopen_the_database()
        {
            using var file = new TempFile();

            SeedUsingDirectMode(file.Filename);

            ReadRowsUsingANewDirectConnection(file.Filename)
                .Should().Equal("1:direct-control");
        }

        [WindowsAppContainerFact]
        public void Shared_mode_in_an_app_container_uses_a_local_mutex_and_persists_data()
        {
            using var file = new TempFile();
            SeedUsingDirectMode(file.Filename);

            var mutexName = GetExpectedDefaultMutexName(file.Filename);
            using var database = new LiteDatabase(new ConnectionString
            {
                Filename = file.Filename,
                Connection = ConnectionType.Shared
            });
            using var localMutex = Mutex.OpenExisting("Local\\" + mutexName + ".Mutex");
            using var workerStarted = new ManualResetEventSlim();
            using var workerFinished = new ManualResetEventSlim();
            Exception workerFailure = null;
            var acquired = false;
            var startedInTime = false;
            var finishedWhileMutexWasHeld = false;

            var worker = new Thread(() =>
            {
                workerStarted.Set();

                try
                {
                    database.GetCollection("rows").Insert(new BsonDocument
                    {
                        ["_id"] = 2,
                        ["value"] = "shared-app-container"
                    });
                }
                catch (Exception ex)
                {
                    workerFailure = ex;
                }
                finally
                {
                    workerFinished.Set();
                }
            })
            {
                IsBackground = true,
                Name = "Issue2793-AppContainerWriter"
            };

            try
            {
                try
                {
                    acquired = localMutex.WaitOne(TimeSpan.FromSeconds(2));
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }

                if (acquired)
                {
                    worker.Start();
                    startedInTime = workerStarted.Wait(TimeSpan.FromSeconds(5));
                    finishedWhileMutexWasHeld = startedInTime &&
                        workerFinished.Wait(TimeSpan.FromMilliseconds(750));
                }
            }
            finally
            {
                if (acquired)
                {
                    localMutex.ReleaseMutex();
                }
            }

            var finishedAfterRelease = acquired &&
                workerFinished.Wait(TimeSpan.FromSeconds(10));
            var joined = !worker.IsAlive || worker.Join(TimeSpan.FromSeconds(2));

            using (new AssertionScope())
            {
                acquired.Should().BeTrue("the independently opened Local mutex must be usable");
                startedInTime.Should().BeTrue("the writer must reach the shared operation");
                finishedWhileMutexWasHeld.Should().BeFalse(
                    "Shared mode must participate in the independently held Local mutex");
                finishedAfterRelease.Should().BeTrue(
                    "the shared write must complete once the Local mutex is released");
                joined.Should().BeTrue("the bounded writer thread must terminate");
                workerFailure.Should().BeNull();
            }

            database.Dispose();

            ReadRowsUsingANewDirectConnection(file.Filename)
                .Should().Equal("1:direct-control", "2:shared-app-container");
        }

        private static void SeedUsingDirectMode(string filename)
        {
            using var database = new LiteDatabase(new ConnectionString
            {
                Filename = filename,
                Connection = ConnectionType.Direct
            });

            database.GetCollection("rows").Insert(new BsonDocument
            {
                ["_id"] = 1,
                ["value"] = "direct-control"
            });
        }

        private static string[] ReadRowsUsingANewDirectConnection(string filename)
        {
            using var database = new LiteDatabase(new ConnectionString
            {
                Filename = filename,
                Connection = ConnectionType.Direct
            });

            return database.GetCollection("rows")
                .FindAll()
                .OrderBy(x => x["_id"].AsInt32)
                .Select(x => x["_id"].AsInt32 + ":" + x["value"].AsString)
                .ToArray();
        }

        private static string GetExpectedDefaultMutexName(string filename)
        {
            var normalized = Path.GetFullPath(filename).ToLowerInvariant();
            var escaped = Uri.EscapeDataString(normalized);

            if (escaped.Length + 13 <= 250)
            {
                return escaped;
            }

            using var sha1 = SHA1.Create();
            var hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(normalized));

            return "sha1-" + BitConverter.ToString(hash).Replace("-", string.Empty);
        }
    }

    internal sealed class WindowsAppContainerFactAttribute : FactAttribute
    {
        public WindowsAppContainerFactAttribute()
        {
            Skip = AppContainerProbe.GetSkipReason();
        }
    }

    internal static class AppContainerProbe
    {
        private const uint TokenQuery = 0x0008;
        private const int TokenIsAppContainer = 29;

        public static string GetSkipReason()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return "Issue 2793 requires a real Windows AppContainer token.";
            }

            if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token))
            {
                return "Could not query the process token (Win32 " +
                    Marshal.GetLastWin32Error() + ").";
            }

            try
            {
                if (!GetTokenInformation(
                    token,
                    TokenIsAppContainer,
                    out var isAppContainer,
                    sizeof(int),
                    out _))
                {
                    return "Could not query TokenIsAppContainer (Win32 " +
                        Marshal.GetLastWin32Error() + ").";
                }

                return isAppContainer == 0
                    ? "Issue 2793 requires a real Windows AppContainer token."
                    : null;
            }
            finally
            {
                CloseHandle(token);
            }
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(
            IntPtr processHandle,
            uint desiredAccess,
            out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetTokenInformation(
            IntPtr tokenHandle,
            int tokenInformationClass,
            out int tokenInformation,
            int tokenInformationLength,
            out int returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
