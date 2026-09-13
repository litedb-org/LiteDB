using System.IO;
using System.Text;
using LiteDB.Engine;

namespace LiteDB.Tests
{
    internal static class FixtureFileVersion
    {
        // These fixtures exercise recovery of existing page contents. Rebuilding them to
        // migrate the version would remove the conditions those tests need to validate.
        internal static void UseCurrentVersion(string filename, string password = null)
        {
            if (!File.Exists(filename)) return;
            using var factory = new FileStreamFactory(filename, password, false, false);
            using var stream = factory.GetStream(true, false);
            var page = new byte[Constants.PAGE_SIZE];
            for (long offset = 0; offset + page.Length <= stream.Length; offset += page.Length)
            {
                stream.Position = offset;
                stream.Read(page, 0, page.Length);
                if (page[HeaderPage.P_FILE_VERSION] == 8 &&
                    Encoding.UTF8.GetString(page, HeaderPage.P_HEADER_INFO, HeaderPage.HEADER_INFO.Length) == HeaderPage.HEADER_INFO)
                {
                    page[HeaderPage.P_FILE_VERSION] = HeaderPage.FILE_VERSION;
                    stream.Position = offset;
                    stream.Write(page, 0, page.Length);
                }
            }
        }
    }
}
