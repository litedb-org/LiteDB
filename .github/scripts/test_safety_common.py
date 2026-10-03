import json
import unittest

import safety_common as common


class ParseTestsTests(unittest.TestCase):
    def test_finds_facts_and_theories_with_namespaces_and_nested_classes(self):
        source = """
namespace LiteDB.Tests.Issues;

public class Outer_Tests
{
    private class Helper { public void NotATest() { } }

    [Fact]
    public void Plain() { }

    [Theory]
    [InlineData(1)]
    public async Task WithData(int value) => await Task.Yield();

    public class Nested
    {
        [Fact(DisplayName = "x")]
        public void Inner<T>() { }
    }
}
"""
        tests = common.parse_tests(source)
        self.assertEqual(
            sorted(tests),
            ["LiteDB.Tests.Issues.Outer_Tests+Nested.Inner",
             "LiteDB.Tests.Issues.Outer_Tests.Plain",
             "LiteDB.Tests.Issues.Outer_Tests.WithData"])

    def test_merges_preprocessor_variants_and_reads_skip_reasons(self):
        source = """
namespace A
{
    public class B
    {
#if NETFRAMEWORK
        [Fact(Skip = "framework keeps the old contract")]
#else
        [Fact]
#endif
        public void Variant() { }

        [MappedFact]
        public void Conditional() { }
    }
}
"""
        tests = common.parse_tests(source)
        variant = tests["A.B.Variant"]
        self.assertEqual(variant.skips, ["framework keeps the old contract", None])
        self.assertEqual(variant.skipped_variants, 1)
        self.assertEqual(tests["A.B.Conditional"].kinds, ["MappedFact"])

    def test_ignores_attributes_inside_comments_and_strings(self):
        source = """
namespace A
{
    public class B
    {
        // [Fact] public void Commented() { }
        /* [Fact]
           public void Blocked() { } */
        private const string Text = "[Fact] public void InString() { }";
        private const string Verbatim = @"[Fact] ""quoted"" public void InVerbatim() { }";
        private const char Brace = '{';

        [Fact]
        public void Real() { var s = $"{Text} [Fact]"; }
    }
}
"""
        self.assertEqual(list(common.parse_tests(source)), ["A.B.Real"])

    def test_body_span_covers_the_method_only(self):
        source = "namespace A { public class B {\n[Fact]\npublic void One() { Call(\"one\"); }\n[Fact]\npublic void Two() { }\n} }"
        tests = common.parse_tests(source)
        one = tests["A.B.One"]
        self.assertIn('"one"', source[one.start:one.end])
        self.assertNotIn("Two", source[one.start:one.end])


class TreeTests(unittest.TestCase):
    def test_reading_a_directory_does_not_desynchronize_later_reads(self):
        from safety_fixtures import GitRepo
        with GitRepo() as repo:
            revision = repo.commit({"dir/a.txt": "alpha", "b.txt": "beta"})
            tree = common.Tree(revision)
            self.assertIsNone(tree.read("dir"))
            self.assertIsNone(tree.read("missing.txt"))
            self.assertEqual(tree.read("b.txt"), "beta")
            self.assertEqual(tree.read("dir/a.txt"), "alpha")
            tree.close()


class HelperTests(unittest.TestCase):
    def test_blank_code_keeps_offsets_and_optionally_strings(self):
        source = 'call("x"); // note\nnext();'
        self.assertEqual(len(common.blank_code(source)), len(source))
        self.assertNotIn('"x"', common.blank_code(source))
        self.assertIn('"x"', common.blank_code(source, keep_strings=True))
        self.assertNotIn("note", common.blank_code(source, keep_strings=True))

    def test_interpolation_holes_with_nested_literals_stay_balanced(self):
        sources = [
            'var m = $"payload {(mode ? "{\\"a\\":1}" : "{}")} done"; call();',
            'var m = $@"a {{literal}} {Format("}", \'{\')} ""q"" {x:0.00}"; call();',
            'var m = $"{(a ? $"{b("}")}" : "(")}"; call();',
        ]
        for source in sources:
            with self.subTest(source):
                code = common.blank_code(source)
                self.assertEqual((code.count("("), code.count(")")), (1, 1), repr(code))
                self.assertEqual((code.count("{"), code.count("}")), (0, 0), repr(code))
                self.assertTrue(code.rstrip().endswith("call();"), repr(code))

    def test_raw_string_literals_are_blanked(self):
        source = 'var s = """\n{ not code }\n""";\nnext();'
        self.assertNotIn("{", common.blank_code(source))

    def test_glob_regex_supports_double_star(self):
        self.assertTrue(common.glob_regex("LiteDB/Engine/Disk/**").match("LiteDB/Engine/Disk/Streams/AesStream.cs"))
        self.assertTrue(common.glob_regex("**/*.cs").match("A.cs"))
        self.assertFalse(common.glob_regex("LiteDB/Engine/*.cs").match("LiteDB/Engine/Disk/DiskService.cs"))
        self.assertTrue(common.glob_regex("LiteDB/Engine/Services/Snap*.cs").match("LiteDB/Engine/Services/SnapShot.cs"))

    def test_markdown_anchors_follow_github_slugs(self):
        text = "# Title\n## v13: snapshot checkpointing and retirement\n## Repeat\n## Repeat\n"
        self.assertEqual(common.markdown_anchors(text),
                         {"title", "v13-snapshot-checkpointing-and-retirement", "repeat", "repeat-1"})


if __name__ == "__main__":
    unittest.main()


class NetModeTests(unittest.TestCase):
    def test_switch_is_read_from_the_scripts_repository_outside_a_checkout(self):
        import os
        import tempfile
        from pathlib import Path
        previous = os.getcwd()
        common._ROOT.clear()
        with tempfile.TemporaryDirectory() as outside:
            os.chdir(outside)
            try:
                expected = json.loads((Path(__file__).resolve().parents[2] / common.NET_MODES).read_text())
                listed = "lint-polling" in expected.get("nets", {})
                self.assertEqual(common.net_advisory("lint-polling"), listed and not expected.get("blocking", True))
                self.assertFalse(common.net_advisory("not-a-listed-net"))
                self.assertTrue(common.net_advisory("lint-polling", blocking=False))
            finally:
                os.chdir(previous)
                common._ROOT.clear()
