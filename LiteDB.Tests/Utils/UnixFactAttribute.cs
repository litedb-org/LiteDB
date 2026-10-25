using System.Runtime.InteropServices;
using Xunit;

namespace LiteDB.Tests.Utils;

/// <summary>A test of Unix-only behaviour: reported as skipped on Windows, not as passed.</summary>
class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Skip = "Unix only.";
        }
    }
}
