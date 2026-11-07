#if NET8_0_OR_GREATER
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Internals;
using Xunit;

namespace LiteDB.Tests.Internals
{
    public class GroupCommitReviewCrash_Tests
    {
        [Theory]
        [InlineData("before-first-sync")]
        [InlineData("after-first-ack")]
        public async Task Sigkill_with_appended_followers_preserves_acknowledgements_and_atomic_indexed_rows(string phase)
        {
            using var file = new TempFile();
            using (var process = new MvccProcess("group-review-kill", file.Filename, null, phase))
            {
                await process.Expect("ready");
                await process.Kill();
            }
            // Process death retains the OS cache: pending transactions may recover.
            // It never proves physical power-loss durability.
            for (var open = 0; open < 2; open++)
            {
                using var db = new LiteDatabase(file.Filename);
                var rows = db.GetCollection("rows");
                var ids = rows.FindAll().Select(row => row["_id"].AsInt32).ToArray();
                ids.Should().Contain(0).And.OnlyHaveUniqueItems().And.OnlyContain(id => id >= 0 && id <= 4);
                if (phase == "after-first-ack") ids.Should().Contain(1);
                rows.FindAll().Should().OnlyContain(row => row["_id"] == row["value"]);
                rows.Find(Query.GT("value", 0)).Select(row => row["_id"].AsInt32)
                    .Should().BeEquivalentTo(ids.Where(id => id > 0));
                db.Checkpoint();
            }
        }
    }
}
#endif
