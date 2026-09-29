using FluentAssertions;
using LiteDB.Engine;
using System;
using System.IO;
using System.Linq;

using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2417_Tests
    {
        [Fact]
        public void Rebuild_Detected_Infinite_Loop()
        {
            var original = "../../../Resources/Issue2417_MyData.db";

            using (var filename = new TempFile(original))
            {
                var before = File.ReadAllBytes(filename);
                var settings = new EngineSettings
                {
                    Filename = filename,
                    AutoRebuild = false,
                };

                try
                {
                    using (var db = new LiteEngine(settings))
                    {
                        // infinite loop here
                        var col = db.Query("customers", Query.All()).ToList();

                        // never run here
                        Assert.Fail("not expected");
                    }
                }
                catch (Exception ex)
                {
                    Assert.True(ex is LiteException lex && lex.ErrorCode == 999, ex.ToString());
                }

                // The refused open is inspection only: neither the rebuild mark nor anything else was written.
                File.ReadAllBytes(filename).Should().Equal(before);

                // The first AutoRebuild open detects the loop and rebuilds in the same open.
                settings.AutoRebuild = true;
                using (var db = new LiteEngine(settings))
                {
                    var col = db.Query("customers", Query.All()).ToList().Count;
                    var errors = db.Query("_rebuild_errors", Query.All()).ToList().Count;

                    col.Should().Be(4);
                    errors.Should().Be(1, "the opening corruption diagnostic is retained in the recovery report");
                    var opening = db.Query("_rebuild_errors", Query.All()).ToList().Single();
                    opening["stage"].AsString.Should().Be("opening");
                    opening["pageType"].AsString.Should().Be("Index");
                    opening["pageID"].IsInt32.Should().BeTrue();
                }

                File.ReadAllBytes(FileHelper.GetSuffixFile(filename, "-backup", false)).Should().Equal(before);
            }
        }

        [Fact]
        public void Rebuild_Detected_Infinite_Loop_With_Password()
        {
            var original = "../../../Resources/Issue2417_TestCacheDb.db";

            using (var filename = new TempFile(original))
            {
                var before = File.ReadAllBytes(filename);
                var settings = new EngineSettings
                {
                    Filename = filename,
                    Password = "bzj2NplCbVH/bB8fxtjEC7u0unYdKHJVSmdmPgArRBwmmGw0+Wd2tE+b2zRMFcHAzoG71YIn/2Nq1EMqa5JKcQ==",
                    AutoRebuild = false,
                };

                try
                {
                    using (var db = new LiteEngine(settings))
                    {
                        // infinite loop here
                        var col = db.Query("hubData$AppOperations", Query.All()).ToList();

                        // never run here
                        Assert.Fail("not expected");
                    }
                }
                catch (Exception ex)
                {
                    Assert.True(ex is LiteException lex && lex.ErrorCode == 999, ex.ToString());
                }

                File.ReadAllBytes(filename).Should().Equal(before);

                settings.AutoRebuild = true;
                using (var db = new LiteEngine(settings))
                {
                    var col = db.Query("hubData$AppOperations", Query.All()).ToList().Count;
                    var errors = db.Query("_rebuild_errors", Query.All()).ToList().Count;

                    col.Should().Be(408);
                    errors.Should().Be(1, "the opening corruption diagnostic is retained in the recovery report");
                }

                File.ReadAllBytes(FileHelper.GetSuffixFile(filename, "-backup", false)).Should().Equal(before);
            }
        }
    }
}

