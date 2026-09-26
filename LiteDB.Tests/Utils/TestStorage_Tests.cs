using System;
using System.Diagnostics;
using System.IO;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests
{
    public class TestStorage_Tests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("disk")]
        public void CI_preserves_original_temp_volume(string mode)
        {
            TestStorage.SelectRoot(mode, "/ignored", true, "original-temp", "/ram")
                .Should().Be("original-temp");
        }

        [Fact]
        public void Local_runs_prefer_RAM_and_disk_requires_an_explicit_opt_in()
        {
            TestStorage.SelectRoot(null, null, false, "disk", "/ram").Should().Be("/ram");
            TestStorage.SelectRoot("disk", null, false, "disk", "/ram").Should().Be("disk");
            Action missing = () => TestStorage.SelectRoot(null, null, false, "disk", null);
            missing.Should().Throw<InvalidOperationException>().WithMessage("*LITEDB_TEST_TEMP_ROOT*");
            Action typo = () => TestStorage.SelectRoot("rma", null, true, "disk", "/ram");
            typo.Should().Throw<ArgumentException>();
            Action relative = () => TestStorage.SelectRoot("ram", "relative", false, "disk", "/ram");
            relative.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void Explicit_RAM_root_can_override_CI_for_harness_validation()
        {
            var root = Path.GetPathRoot(Path.GetFullPath("."));
            TestStorage.SelectRoot("ram", root, true, "disk", null).Should().Be(root);
        }

        [Fact]
        public void Temp_paths_and_file_reopen_use_the_selected_root()
        {
            var mode = Environment.GetEnvironmentVariable("LITEDB_TEST_STORAGE");
            var configuredRoot = Environment.GetEnvironmentVariable("LITEDB_TEST_TEMP_ROOT");
            // The subprocess harness forces this branch even on CI, where the full
            // suite intentionally uses disk. Check routing independently of TMP.
            if (mode == "ram" && !string.IsNullOrEmpty(configuredRoot))
                Directory.GetParent(TestStorage.Root).FullName.Should().Be(Path.GetFullPath(configuredRoot).TrimEnd(Path.DirectorySeparatorChar));
            Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                .Should().Be(Path.GetFullPath(TestStorage.Root).TrimEnd(Path.DirectorySeparatorChar));
            using var file = new TempFile();
            Path.GetDirectoryName(file.Filename).Should().Be(Path.GetFullPath(TestStorage.Root).TrimEnd(Path.DirectorySeparatorChar));
            using (var db = new LiteDatabase(file.Filename))
            {
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "committed" });
            }
            using var reopened = new LiteDatabase(file.Filename);
            reopened.GetCollection("rows").FindById(1)["value"].AsString.Should().Be("committed");
        }

        [Fact]
        public void Child_process_inherits_the_test_temp_directory()
        {
            var windows = Environment.OSVersion.Platform == PlatformID.Win32NT;
            using var child = Process.Start(new ProcessStartInfo
            {
                FileName = windows ? "cmd.exe" : "/bin/sh",
                Arguments = windows ? "/d /c echo %TEMP%" : "-c \"printf '%s' \\\"$TMPDIR\\\"\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            });
            var output = child.StandardOutput.ReadToEnd().Trim();
            child.WaitForExit(5000).Should().BeTrue();
            child.ExitCode.Should().Be(0);
            // On CI Unix, TMPDIR may legitimately be unset; setup preserves it.
            output.Should().Be(Environment.GetEnvironmentVariable(windows ? "TEMP" : "TMPDIR") ?? "");
        }

        [Fact]
        public void Memory_database_preserves_commits_and_indexes_but_not_rolled_back_rows_across_reopens()
        {
            using var storage = new MemoryDatabase();
            using (var db = storage.Open())
            {
                var rows = db.GetCollection("rows");
                rows.EnsureIndex("name");
                rows.Insert(new BsonDocument { ["_id"] = 1, ["name"] = "committed" });
                db.BeginTrans();
                rows.Insert(new BsonDocument { ["_id"] = 2, ["name"] = "pending" });
                db.Rollback();
            }
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var db = storage.Open();
                db.GetCollection("rows").Count().Should().Be(1);
                db.GetCollection("rows").FindOne(Query.EQ("name", "committed"))["_id"].AsInt32.Should().Be(1);
                Assert.Null(db.GetCollection("rows").FindById(2));
            }
            using var independent = new MemoryDatabase();
            using var empty = independent.Open();
            empty.GetCollection("rows").Count().Should().Be(0);
        }
    }
}
