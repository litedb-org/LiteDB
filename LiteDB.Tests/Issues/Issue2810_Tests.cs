using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2810_Tests
    {
        public class Book { public Guid Id { get; set; } public string Name { get; set; } }
        public class Category { public int Id { get; set; } public List<Book> Books { get; set; } }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Any_Contains_matches_CLR_for_embedded_and_referenced_lists(bool references, bool enumerable)
        {
            var a = new Book { Id = Guid.Parse("39e69cdc-8071-4b81-a8c4-865dd8137fb1"), Name = "a" };
            var b = new Book { Id = Guid.Parse("54370a31-022b-4430-982c-e791032eed8a"), Name = "b" };
            var rows = new[]
            {
                new Category { Id = 1, Books = new List<Book> { a } },
                new Category { Id = 2, Books = new List<Book> { b } },
                new Category { Id = 3, Books = new List<Book> { a, b } },
                new Category { Id = 4, Books = new List<Book>() }
            };
            var mapper = new BsonMapper();
            if (references) mapper.Entity<Category>().DbRef(x => x.Books, "books");
            using var db = new LiteDatabase(":memory:", mapper);
            db.GetCollection<Book>("books").Insert(new[] { a, b });
            var col = db.GetCollection<Category>();
            col.Insert(rows);
            foreach (var ids in new[] { new List<Guid> { a.Id }, new List<Guid> { b.Id }, new List<Guid>(), new List<Guid> { a.Id, b.Id } })
            {
                var expected = rows.Where(x => x.Books.Any(book => ids.Contains(book.Id))).Select(x => x.Id).ToArray();
                if (enumerable)
                {
                    IEnumerable<Guid> captured = ids;
                    col.Find(x => x.Books.Select(book => book.Id).Any(id => captured.Contains(id)))
                        .Select(x => x.Id).OrderBy(x => x).Should().Equal(expected);
                }
                else
                {
                    col.Find(x => x.Books.Select(book => book.Id).Any(id => ids.Contains(id)))
                        .Select(x => x.Id).OrderBy(x => x).Should().Equal(expected);
                    col.Find(x => x.Books.Any(book => ids.Contains(book.Id)))
                        .Select(x => x.Id).OrderBy(x => x).Should().Equal(expected);
                }
            }
            col.Count().Should().Be(4);
        }
    }
}
