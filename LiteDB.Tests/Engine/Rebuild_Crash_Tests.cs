using FluentAssertions;
using LiteDB.Engine;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Xunit;
using Xunit.Abstractions;

#if DEBUG || TESTING
namespace LiteDB.Tests.Engine
{
    public class Rebuild_Crash_Tests
    {
        private readonly ITestOutputHelper _output;

        public Rebuild_Crash_Tests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact(Timeout = 30000)]
        public async Task Rebuild_Crash_IO_Write_Error()
        {
            var testName = nameof(Rebuild_Crash_IO_Write_Error);

            _output.WriteLine($"starting {testName}");

            try
            {
                var N = 1000;

                using (var file = new TempFile())
                {
                    var settings = new EngineSettings
                    {
                        AutoRebuild = true,
                        Filename = file.Filename,
                        Password = "46jLz5QWd5fI3m4LiL2r"
                    };

                    var initial = new DateTime(2024, 1, 1);

                    var data = Enumerable.Range(1, N).Select(i => new BsonDocument
                    {
                        ["_id"] = i,
                        ["name"] = $"user-{i:D4}",
                        ["age"] = 18 + (i % 60),
                        ["created"] = initial.AddDays(i),
                        ["lorem"] = new string((char)('a' + (i % 26)), 800)
                    }).ToArray();

                    var faultInjected = 0;

                    try
                    {
                        using (var db = new LiteEngine(settings))
                        {
                            var writeHits = 0;

                            db.SimulateDiskWriteFail = (page) =>
                            {
                                var p = new BasePage(page);

                                if (p.PageType == PageType.Data && p.ColID == 1)
                                {
                                    var hit = Interlocked.Increment(ref writeHits);

                                    if (hit == 10)
                                    {
                                        p.PageType.Should().Be(PageType.Data);
                                        p.ColID.Should().Be(1);

                                        page.Write((uint)123123123, 8192 - 4);

                                        Interlocked.Exchange(ref faultInjected, 1);
                                    }
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
                        faultInjected.Should().Be(1, "the simulated disk write fault should have triggered");

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

                await Task.CompletedTask;
            }
            finally
            {
                _output.WriteLine($"{testName} completed");
            }
        }
    }
}

#endif
