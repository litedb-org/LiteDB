using FluentAssertions;
using LiteDB.Engine;
using System;
using System.IO;
using System.Linq;

using Xunit;

#if DEBUG
namespace LiteDB.Tests.Engine
{
    public class Rebuild_Crash_Tests
    {

        [Fact]
        public void Rebuild_Crash_IO_Write_Error()
        {
            var N = 1_000;

            using (var file = new TempFile())
            {
                var settings = new EngineSettings
                {
                    AutoRebuild = true,
                    Filename = file.Filename,
                    Password = "46jLz5QWd5fI3m4LiL2r"
                };

                var data = Enumerable.Range(1, N).Select(i => new BsonDocument
                {
                    ["_id"] = i,
                    ["name"] = Faker.Fullname(),
                    ["age"] = Faker.Age(),
                    ["created"] = Faker.Birthday(),
                    ["lorem"] = Faker.Lorem(5, 25)
                }).ToArray();

                try
                {
                    using (var db = new LiteEngine(settings))
                    {
                        db.SimulateDiskWriteFail = (page) =>
                        {
                            var p = new BasePage(page);

                            if (p.PageID == 28)
                            {
                                p.ColID.Should().Be(1);
                                p.PageType.Should().Be(PageType.Data);

                                page.Write((uint)123123123, 8192 - 4);
                            }
                        };

                        db.Pragma("USER_VERSION", 123);

                        db.EnsureIndex("col1", "idx_age", "$.age", false);

                        db.Insert("col1", data, BsonAutoId.Int32);
                        db.Insert("col2", data, BsonAutoId.Int32);

                        db.Checkpoint();

                        // will fail
                        var col1 = db.Query("col1", Query.All()).ToList().Count;

                        // never run here
                        Assert.Fail("should get error in query");
                    }
                }
                catch (Exception ex)
                {
                    Assert.True(ex is LiteException lex && lex.ErrorCode == 999);
                }

                //Console.WriteLine("Recovering database...");

                using (var db = new LiteEngine(settings))
                {
                    var col1 = db.Query("col1", Query.All()).ToList().Count;
                    var col2 = db.Query("col2", Query.All()).ToList().Count;
                    var errors = db.Query("_rebuild_errors", Query.All()).ToList().Count;

                    col1.Should().Be(N - 1);
                    col2.Should().Be(N);
                    errors.Should().Be(1);

                }
            }
        }
    }
}

#endif
