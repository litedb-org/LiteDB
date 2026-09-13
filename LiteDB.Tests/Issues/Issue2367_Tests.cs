using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2367_Tests
    {
        public class Video { public int Id { get; set; } public DateTime Published { get; set; } }

        [Theory]
        [InlineData("list")]
        [InlineData("array")]
        [InlineData("dictionary")]
        public void Captured_element_date_member_matches_CLR_and_tracks_changed_values(string kind)
        {
            var epoch = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var data = Enumerable.Range(0, 5).Select(i => new Video { Id = i + 1, Published = epoch.AddDays(i) }).ToArray();
            var list = new List<Video> { data[1] };
            var array = new[] { data[1] };
            var dictionary = new Dictionary<string, Video> { ["cutoff"] = data[1] };
            Expression<Func<Video, bool>> predicate = kind == "list"
                ? x => x.Published > list[0].Published
                : kind == "array" ? x => x.Published > array[0].Published
                : x => x.Published > dictionary["cutoff"].Published;
            using var db = new LiteDatabase(":memory:");
            var col = db.GetCollection<Video>("videos");
            col.InsertBulk(data);
            foreach (var cutoff in new[] { 1, 3, 0 })
            {
                list[0] = array[0] = dictionary["cutoff"] = data[cutoff];
                var expected = data.Where(predicate.Compile()).Select(x => x.Id).ToArray();
                col.Find(predicate).Select(x => x.Id).OrderBy(x => x).Should().Equal(expected);
            }
            col.FindAll().OrderBy(x => x.Id).Select(x => x.Published.ToUniversalTime()).Should().Equal(data.Select(x => x.Published));
        }
    }
}
