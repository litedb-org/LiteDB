using System;
using System.IO;

namespace LiteDB.Tests
{
    internal static class TestResource
    {
        internal static string GetPath(string filename)
        {
            var published = Path.Combine(AppContext.BaseDirectory, "Resources", filename);
            return File.Exists(published) ? published : Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "Resources", filename));
        }
    }
}
