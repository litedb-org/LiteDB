using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2894_Tests
    {
        public class EnumerableEntity : IEnumerable<string>
        {
            public int Id { get; set; }
            public string Name { get; set; }

            public IEnumerator<string> GetEnumerator()
            {
                yield return this.Name;
            }

            IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();
        }

        public class ErrorLog
        {
            public int Id { get; set; }
            public Exception Error { get; set; }
        }

        public class StreamHolder
        {
            public int Id { get; set; }
            public Stream Data { get; set; }
        }

        [Fact]
        [Trait("Category", "PendingBug")]
        public void Enumerable_root_entity_is_mapped_or_rejected_with_its_type_name()
        {
            using var db = new LiteDatabase(new MemoryStream());
            Action insert = () => db.GetCollection<EnumerableEntity>("bags")
                .Insert(new EnumerableEntity { Id = 1, Name = "bag" });

            try
            {
                insert();
                Assert.NotNull(db.GetCollection("bags").FindById(1));
            }
            catch (Exception error)
            {
                error.Should().BeOfType<LiteException>();
                error.Message.Should().Contain(nameof(EnumerableEntity));
            }
        }

        [Fact]
        [Trait("Category", "PendingBug")]
        public void Exception_member_failure_names_the_entity_and_member()
        {
            Exception captured;
            try
            {
                throw new InvalidOperationException("boom");
            }
            catch (Exception error)
            {
                captured = error;
            }

            using var db = new LiteDatabase(new MemoryStream());
            Action insert = () => db.GetCollection<ErrorLog>()
                .Insert(new ErrorLog { Id = 1, Error = captured });

            AssertContextualFailure(insert, nameof(ErrorLog), nameof(ErrorLog.Error));
        }

        [Fact]
        [Trait("Category", "PendingBug")]
        public void Stream_member_failure_names_the_entity_and_member()
        {
            using var db = new LiteDatabase(new MemoryStream());
            Action insert = () => db.GetCollection<StreamHolder>()
                .Insert(new StreamHolder { Id = 1, Data = new MemoryStream() });

            AssertContextualFailure(insert, nameof(StreamHolder), nameof(StreamHolder.Data));
        }

        private static void AssertContextualFailure(Action action, string entity, string member)
        {
            var failure = action.Should().Throw<LiteException>().Which;
            failure.Message.Should().Contain(entity);
            failure.Message.Should().Contain(member);
            failure.InnerException.Should().NotBeNull();
        }
    }
}
