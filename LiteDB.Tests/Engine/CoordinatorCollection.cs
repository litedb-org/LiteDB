#if !NETFRAMEWORK
using System;
using System.IO;
using System.Runtime.InteropServices;
using LiteDB.Client.Coordinated;
using Xunit;

namespace LiteDB.Tests.Engine
{
    [CollectionDefinition(CoordinatorPlatform_Tests.Collection)]
    public class CoordinatorCollection : ICollectionFixture<CoordinatorStatusPageBase>
    {
    }

    /// <summary>
    /// The status page tests need a base directory nobody else can write to. A service
    /// account (a CI runner) may have neither a private TMPDIR nor XDG_RUNTIME_DIR; the page
    /// is then unavailable by design. The collection then provides an owner-only
    /// XDG_RUNTIME_DIR, as a login session would.
    /// </summary>
    public sealed class CoordinatorStatusPageBase : IDisposable
    {
        private const string Variable = "XDG_RUNTIME_DIR";
        private readonly string _created;
        private readonly string _previous;

        public CoordinatorStatusPageBase()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
            try
            {
                CoordinatorStatusPage.PathFor("probe.db");
                return;
            }
            catch (UnauthorizedAccessException) { }

            _previous = Environment.GetEnvironmentVariable(Variable);
            _created = Path.Combine(Path.GetTempPath(), "litedb-runtime-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_created, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Environment.SetEnvironmentVariable(Variable, _created);
        }

        public void Dispose()
        {
            if (_created == null) return;
            Environment.SetEnvironmentVariable(Variable, _previous);
            try { Directory.Delete(_created, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
#endif
