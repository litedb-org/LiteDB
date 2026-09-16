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
        public class Category { public int Id { get; set; } public List<Book> Books { get; set; } public Guid[] AllowedIds { get; set; } }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Compound_predicates_keep_each_item_id_mapping_across_root_and_repeated_accesses(bool references)
        {
            var a = new Book { Id = Guid.NewGuid(), Name = "a" };
            var b = new Book { Id = Guid.NewGuid(), Name = "b" };
            var rows = new[]
            {
                new Category { Id = 1, Books = new List<Book> { a }, AllowedIds = new[] { a.Id } },
                new Category { Id = 2, Books = new List<Book> { b }, AllowedIds = new[] { a.Id } },
                new Category { Id = 3, Books = new List<Book> { a, b }, AllowedIds = new[] { b.Id } },
                new Category { Id = 4, Books = new List<Book>(), AllowedIds = new Guid[0] }
            };
            var mapper = new BsonMapper();
            if (references) mapper.Entity<Category>().DbRef(x => x.Books, "books");
            using var db = new LiteDatabase(":memory:", mapper);
            db.GetCollection<Book>("books").Insert(new[] { a, b });
            var col = db.GetCollection<Category>();
            col.Insert(rows);
            col.Find(x => x.Books.Any(book => x.AllowedIds.Contains(book.Id))).Select(x => x.Id).Should().Equal(1, 3);
            col.Find(x => x.Books.Any(book => book.Id == a.Id || book.Id == b.Id)).Select(x => x.Id).Should().Equal(1, 2, 3);
            col.Find(x => x.Books.Any(book => book.Id == a.Id && book.Id != b.Id)).Select(x => x.Id).Should().Equal(1, 3);
            col.Find(x => x.Books.All(book => book.Id != a.Id && book.Id == b.Id)).Select(x => x.Id).Should().Equal(2, 4);
            col.Find(x => x.Books.Any(book => x.Id > 0 && x.AllowedIds.Contains(book.Id))).Select(x => x.Id).Should().Equal(1, 3);
            var captured = new[] { a.Id };
            col.Find(x => x.Books.Where(book => book.Id != Guid.Empty).Select(book => book.Id)
                .Any(id => captured.Contains(id))).Select(x => x.Id).Should().Equal(1, 3);
            col.Find(x => x.Books.Where(book => book.Id != Guid.Empty).Select(book => book)
                .Any(book => captured.Contains(book.Id))).Select(x => x.Id).Should().Equal(1, 3);
            col.Find(x => x.Books.Where(book => book.Id != Guid.Empty && book.Id != b.Id)
                .Any(book => x.AllowedIds.Contains(book.Id))).Select(x => x.Id).Should().Equal(1);
        }

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
                var expectedAny = rows.Where(x => x.Books.Any(book => ids.Contains(book.Id))).Select(x => x.Id).ToArray();
                var expectedAll = rows.Where(x => x.Books.All(book => ids.Contains(book.Id))).Select(x => x.Id).ToArray();
                if (enumerable)
                {
                    IEnumerable<Guid> captured = ids;
                    col.Find(x => x.Books.Select(book => book.Id).Any(id => captured.Contains(id)))
                        .Select(x => x.Id).OrderBy(x => x).Should().Equal(expectedAny);
                    col.Find(x => x.Books.Any(book => captured.Contains(book.Id)))
                        .Select(x => x.Id).OrderBy(x => x).Should().Equal(expectedAny);
                    col.Find(x => x.Books.Select(book => book.Id).All(id => captured.Contains(id)))
                        .Select(x => x.Id).OrderBy(x => x).Should().Equal(expectedAll);
                    col.Find(x => x.Books.All(book => captured.Contains(book.Id)))
                        .Select(x => x.Id).OrderBy(x => x).Should().Equal(expectedAll);
                }
                else
                {
                    col.Find(x => x.Books.Select(book => book.Id).Any(id => ids.Contains(id)))
                        .Select(x => x.Id).OrderBy(x => x).Should().Equal(expectedAny);
                    col.Find(x => x.Books.Any(book => ids.Contains(book.Id)))
                        .Select(x => x.Id).OrderBy(x => x).Should().Equal(expectedAny);
                    col.Find(x => x.Books.Select(book => book.Id).All(id => ids.Contains(id)))
                        .Select(x => x.Id).OrderBy(x => x).Should().Equal(expectedAll);
                    col.Find(x => x.Books.All(book => ids.Contains(book.Id)))
                        .Select(x => x.Id).OrderBy(x => x).Should().Equal(expectedAll);
                }
            }
            col.Count().Should().Be(4);
        }
    }
}
