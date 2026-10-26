#if !NETFRAMEWORK
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using LiteDB.Internals;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class AllocationProcess_Tests
    {
        public static IEnumerable<object[]> Cases()
        {
            foreach (var password in new[] { null, "secret" })
            foreach (var mode in new[] { "hole", "tail", "free" })
            foreach (var limit in new[] { 1, 1000 })
                yield return new object[] { password, mode, limit };
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public async Task Killed_allocator_preserves_acknowledged_rows_and_page_graph(string password, string mode, int limit)
        {
            using var file = new TempFile();
            using (var child = new MvccProcess("allocation-publication", file.Filename, password, mode + " " + limit + " published"))
            {
                await child.Expect("ready");
                await child.Kill();
            }
            for (var retry = 0; retry < 2; retry++)
            {
                using var db = AllocationWorkload.Open(file.Filename, password);
                AllocationWorkload.Verify(db);
                db.Checkpoint();
            }
            AllocationReachability.Verify(file.Filename, password);
        }
    }
}
#endif
