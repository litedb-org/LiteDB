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
        public class Video : IComparable<Video>
        {
            public int Id { get; set; }
            public DateTime Published { get; set; }
            public int CompareTo(Video other) => Published.CompareTo(other.Published);
        }

        public class WrappedVideo
        {
            public Video Value { get; set; }
            public static implicit operator Video(WrappedVideo wrapped) => wrapped.Value;
        }

        public class Videos : List<Video>
        {
            public Video First() => this[0];
        }

        public class VideoIndexer
        {
            public Video Value { get; set; }
            public int Reads { get; private set; }
            public Video this[int index]
            {
                get
                {
                    Reads++;
                    return Value;
                }
            }
        }

        [Fact]
        public void Captured_indexer_is_read_once_and_multidimensional_array_members_are_supported()
        {
            var first = new Video { Id = 1, Published = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
            var second = new Video { Id = 2, Published = first.Published.AddDays(1) };
            var indexer = new VideoIndexer { Value = first };
            var matrix = new Video[,] { { first, second } };
            using var db = new LiteDatabase(":memory:");
            var col = db.GetCollection<Video>();
            col.Insert(new[] { first, second });
            col.Find(x => x.Published > indexer[0].Published).Select(x => x.Id).Should().Equal(2);
            indexer.Reads.Should().Be(1);
            col.Find(x => x.Published == matrix[0, 1].Published).Select(x => x.Id).Should().Equal(2);
            col.Find(x => x.Published.Day == matrix[0, 1].Published.Day).Select(x => x.Id).Should().Equal(2);
        }

        [Fact]
        public void Captured_index_arguments_do_not_freeze_server_clock_members()
        {
            var offsets = new[] { 1 };
            var expression = new BsonMapper().GetExpression<Video, int>(x => DateTime.Now.AddDays(offsets[0]).Year);
            expression.Source.Should().Contain("NOW()");
        }

        [Fact]
        public void Captured_pipeline_preserves_server_runtime_in_selectors()
        {
            var offsets = new[] { 1 };
            var mapper = new BsonMapper();
            var expression = mapper.GetExpression<Video, int>(x => offsets
                .Select(offset => DateTime.Now.AddDays(offset)).First(date => date.Year > 2000 && date.Year < 9999).Year);
            expression.Source.Should().Contain("NOW()");
            expression.ExecuteScalar(new BsonDocument()).AsInt32.Should().Be(DateTime.Now.AddDays(1).Year);

            // Indexing a MAP result is unsupported in the existing BSON grammar. Keep
            // rejecting that shape rather than silently freezing its clock as a parameter.
            Action unsupported = () => mapper.GetExpression<Video, int>(x => offsets
                .Select(offset => DateTime.Now.AddDays(offset)).First().Year);
            unsupported.Should().Throw<NotSupportedException>();
        }

        [Fact]
        public void Deterministic_constants_in_captured_pipelines_are_bound_as_values()
        {
            var one = new[] { 0 };
            var mapper = new BsonMapper { EmptyStringToNull = false };
            mapper.GetExpression<Video, Guid>(x => one.Select(_ => Guid.Empty).First())
                .ExecuteScalar(new BsonDocument()).AsGuid.Should().Be(Guid.Empty);
            mapper.GetExpression<Video, ObjectId>(x => one.Select(_ => ObjectId.Empty).First())
                .ExecuteScalar(new BsonDocument()).AsObjectId.Should().Be(ObjectId.Empty);
            mapper.GetExpression<Video, string>(x => one.Select(_ => string.Empty).First())
                .ExecuteScalar(new BsonDocument()).AsString.Should().BeEmpty();
        }

        [Fact]
        public void Unsupported_runtime_captures_fail_explicitly()
        {
            var dates = new DateTime[32];
            var videos = new[] { new Video { Published = DateTime.MinValue } };
            var mapper = new BsonMapper();
            Action index = () => mapper.GetExpression<Video, DateTime>(x => dates[DateTime.Now.Day]);
            Action member = () => mapper.GetExpression<Video, DateTime>(x => videos.First(v => v.Published < DateTime.Now).Published);
            index.Should().Throw<NotSupportedException>();
            member.Should().Throw<NotSupportedException>();
            Action mixed = () => mapper.GetExpression<Video, DateTime>(x => videos[x.Id].Published);
            mixed.Should().Throw<NotSupportedException>();
            Action wrapper = () => mapper.GetExpression<Video, int>(x => dates
                .Select(d => DateTime.Now).First(d => d.Year > 2000).CompareTo(DateTime.MinValue));
            wrapper.Should().Throw<NotSupportedException>();
            var unchosen = new VideoIndexer();
            var useClock = true;
            Action conditional = () => mapper.GetExpression<Video, int>(x =>
                (useClock ? DateTime.Now : unchosen[0].Published).Year);
            conditional.Should().Throw<NotSupportedException>();
            Action shortCircuit = () => mapper.GetExpression<Video, int>(x => dates.Select(_ => DateTime.Now)
                .First(d => useClock || unchosen[0].Published < d).Year);
            shortCircuit.Should().Throw<NotSupportedException>();
            Action shortCircuitElement = () => mapper.GetExpression<Video, DateTime>(x => dates.Select(_ => DateTime.Now)
                .First(d => useClock || unchosen[0].Published < d));
            Action convertedConditional = () => mapper.GetExpression<Video, double>(x =>
                (double)(useClock ? DateTime.Now.Year : unchosen[0].Id));
            shortCircuitElement.Should().Throw<NotSupportedException>();
            convertedConditional.Should().Throw<NotSupportedException>();
            unchosen.Reads.Should().Be(0);
        }

        [Fact]
        public void Unsupported_runtime_unary_operators_and_root_branches_fail_before_capture_reads()
        {
            var offsets = new[] { 1 };
            var mapper = new BsonMapper();
            Action negative = () => mapper.GetExpression<Video, int>(x => -(DateTime.Now.Day + offsets[0]));
            Action complement = () => mapper.GetExpression<Video, int>(x => ~(DateTime.Now.Day + offsets[0]));
            Action narrow = () => mapper.GetExpression<Video, byte>(x => (byte)(DateTime.Now.Year + offsets[0]));
            Action checkedCast = () => mapper.GetExpression<Video, byte>(x => checked((byte)(DateTime.Now.Year + offsets[0])));
            negative.Should().Throw<NotSupportedException>();
            complement.Should().Throw<NotSupportedException>();
            narrow.Should().Throw<NotSupportedException>();
            checkedCast.Should().Throw<NotSupportedException>();
            mapper.GetExpression<Video, long>(x => (long)(DateTime.Now.Year + offsets[0])).Source.Should().Contain("NOW()");
            mapper.GetExpression<Video, bool>(x => !(DateTime.Now.Year > offsets[0])).Source.Should().Contain("NOW()");

            var unchosen = new VideoIndexer();
            var useClock = true;
            Action conditional = () => mapper.GetExpression<Video, DateTime>(x => useClock ? DateTime.Now : unchosen[0].Published);
            Action or = () => mapper.GetExpression<Video, bool>(x => DateTime.Now.Year > 0 || unchosen[0].Id > 0);
            Action and = () => mapper.GetExpression<Video, bool>(x => DateTime.Now.Year < 0 && unchosen[0].Id > 0);
            Action coalesce = () => mapper.GetExpression<Video, DateTime>(x => (DateTime?)DateTime.Now ?? unchosen[0].Published);
            conditional.Should().Throw<NotSupportedException>();
            or.Should().Throw<NotSupportedException>();
            and.Should().Throw<NotSupportedException>();
            coalesce.Should().Throw<NotSupportedException>();
            unchosen.Reads.Should().Be(0);
        }

        [Fact]
        public void Conditional_element_members_preserve_short_circuit_evaluation()
        {
            var chosen = new VideoIndexer { Value = new Video { Id = 42 } };
            var unchosen = new VideoIndexer();
            var chooseFirst = true;
            var expression = new BsonMapper().GetExpression<Video, int>(x => (chooseFirst ? chosen[0] : unchosen[0]).Id);
            expression.ExecuteScalar(new BsonDocument()).AsInt32.Should().Be(42);
            chosen.Reads.Should().Be(1);
            unchosen.Reads.Should().Be(0);
        }

        [Theory]
        [InlineData("list")]
        [InlineData("array")]
        [InlineData("dictionary")]
        [InlineData("first")]
        [InlineData("first_or_default")]
        [InlineData("last")]
        [InlineData("last_or_default")]
        [InlineData("single")]
        [InlineData("single_or_default")]
        [InlineData("element_at")]
        [InlineData("element_at_or_default")]
        [InlineData("first_predicate")]
        [InlineData("to_array")]
        [InlineData("where_first")]
        [InlineData("conditional")]
        [InlineData("coalesce")]
        [InlineData("instance_first")]
        [InlineData("min")]
        [InlineData("max")]
        [InlineData("max_selector")]
        [InlineData("conditional_element")]
        [InlineData("coalesced_element")]
        [InlineData("conversion")]
        [InlineData("new_array")]
        public void Captured_element_date_member_matches_CLR_and_tracks_changed_values(string kind)
        {
            var epoch = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var data = Enumerable.Range(0, 5).Select(i => new Video { Id = i + 1, Published = epoch.AddDays(i) }).ToArray();
            var list = new List<Video> { data[1] };
            var instance = new Videos { data[1] };
            List<Video> optional = null;
            var chooseFirst = true;
            Video optionalVideo = null;
            var wrapped = new[] { new WrappedVideo() };
            var array = new[] { data[1] };
            var dictionary = new Dictionary<string, Video> { ["cutoff"] = data[1] };
            Expression<Func<Video, bool>> predicate = kind switch
            {
                "list" => x => x.Published > list[0].Published,
                "array" => x => x.Published > array[0].Published,
                "dictionary" => x => x.Published > dictionary["cutoff"].Published,
                "first" => x => x.Published > list.First().Published,
                "first_or_default" => x => x.Published > list.FirstOrDefault().Published,
                "last" => x => x.Published > list.Last().Published,
                "last_or_default" => x => x.Published > list.LastOrDefault().Published,
                "single" => x => x.Published > list.Single().Published,
                "single_or_default" => x => x.Published > list.SingleOrDefault().Published,
                "element_at" => x => x.Published > list.ElementAt(0).Published,
                "element_at_or_default" => x => x.Published > list.ElementAtOrDefault(0).Published,
                "first_predicate" => x => x.Published > list.First(v => v.Id > 0).Published,
                "to_array" => x => x.Published > list.ToArray()[0].Published,
                "where_first" => x => x.Published > list.Where(v => v.Id > 0).First().Published,
                "conditional" => x => x.Published > (chooseFirst ? list : instance)[0].Published,
                "coalesce" => x => x.Published > (optional ?? list)[0].Published,
                "instance_first" => x => x.Published > instance.First().Published,
                "min" => x => x.Published > list.Min().Published,
                "max" => x => x.Published > list.Max().Published,
                "max_selector" => x => x.Published > list.Max(v => v.Published).Date,
                "conditional_element" => x => x.Published > (chooseFirst ? list[0] : instance[0]).Published,
                "coalesced_element" => x => x.Published > (optionalVideo ?? list[0]).Published,
                "conversion" => x => x.Published > ((Video)wrapped[0]).Published,
                "new_array" => x => x.Published > new[] { list[0] }[0].Published,
                _ => throw new ArgumentException(nameof(kind))
            };
            using var db = new LiteDatabase(":memory:");
            var col = db.GetCollection<Video>("videos");
            col.InsertBulk(data);
            foreach (var cutoff in new[] { 1, 3, 0 })
            {
                list[0] = array[0] = dictionary["cutoff"] = data[cutoff];
                instance[0] = data[(cutoff + 1) % data.Length];
                chooseFirst = !chooseFirst;
                optional = chooseFirst ? instance : null;
                optionalVideo = chooseFirst ? instance[0] : null;
                wrapped[0].Value = list[0];
                var expected = data.Where(predicate.Compile()).Select(x => x.Id).ToArray();
                col.Find(predicate).Select(x => x.Id).OrderBy(x => x).Should().Equal(expected);
            }
            col.FindAll().OrderBy(x => x.Id).Select(x => x.Published.ToUniversalTime()).Should().Equal(data.Select(x => x.Published));
        }
    }
}
