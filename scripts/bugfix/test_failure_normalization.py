import json
from pathlib import Path
import tempfile
import unittest

from failure_normalization import (FailureNormalizationError, canonical_failure,
                                   load_failure_normalization)


POLICY_PATH = Path(__file__).with_name("failure-normalization.json")

OBJECT_ID = (
    "LiteDB.Tests.Issues.Issue2590_Tests."
    "Empty_string_ids_are_addressable_or_rejected_before_any_write(path: InsertOne)")
TEMP_DATABASE = (
    "LiteDB.Tests.Issues.Issue2820_Tests."
    "Writable_open_trims_a_torn_v5_tail_only_after_validating_and_preserves_the_ledger")
TEMP_LOG = (
    "LiteDB.Tests.Issues.Issue1966_Tests."
    "Readonly_index_creation_is_rejected_explicitly_and_existing_data_remains_readable"
    "(mode: Direct, existing: False)")
GENERATED_ID = (
    "LiteDB.Tests.Issues.Issue2324_Tests."
    "Reapplying_same_id_mapping_during_serialization_never_exposes_partial_mapping")
CIPHERTEXT = (
    "LiteDB.Tests.Issues.Issue2777_Tests."
    "Identical_plaintext_pages_do_not_disclose_repeated_ciphertext_blocks")
HASH_CODE = (
    "LiteDB.Tests.Audit2026.ExpressionAuditRegression_Tests."
    "M145_collation_hash_matches_collation_equality")
FOREIGN_FILE = (
    "LiteDB.Tests.Issues.Issue2820_Tests."
    "Opening_a_foreign_file_rejects_it_without_changing_any_byte"
    "(length: 16, readOnly: False)")
WAL_PEAK = (
    "LiteDB.Tests.Issues.Issue2814_Tests."
    "Finite_concurrent_readers_do_not_allow_WAL_growth_far_beyond_checkpoint_budget")
ISSUE_2870_PREFIX = "LiteDB.Tests.Issues.Issue2870_Tests."
STACK_CASES = {
    ISSUE_2870_PREFIX + "Query_preserves_the_original_frame_when_a_later_source_read_throws": (
        [("LiteDB.BsonDataReader.Read()", "LiteDB/Document/DataReader/BsonDataReader.cs", 118),
         ("LiteDB.Tests.Issues.Issue2870_Tests.LaterRead()",
          "LiteDB.Tests/Issues/Issue2870_Tests.cs", 60)],
        "ThrowAtOriginalSourceSite"),
    ISSUE_2870_PREFIX + "FileReaderV8_preserves_the_original_stream_failure_frame": (
        [("LiteDB.Engine.FileReaderV8.HandleError()",
          "LiteDB/Engine/FileReader/FileReaderV8.cs", 576),
         ("LiteDB.Engine.FileReaderV8.Open()",
          "LiteDB/Engine/FileReader/FileReaderV8.cs", 90)],
        "ThrowAtOriginalFileReadSite"),
    ISSUE_2870_PREFIX + "Query_preserves_the_original_frame_when_the_first_source_read_throws": (
        [("LiteDB.Engine.QueryExecutor.RunQuery()", "LiteDB/Engine/Query/QueryExecutor.cs", 139),
         ("LiteDB.EnumerableExtensions.OnDispose()",
          "LiteDB/Utils/Extensions/EnumerableExtensions.cs", 13),
         ("LiteDB.EnumerableExtensions.OnDispose()",
          "LiteDB/Utils/Extensions/EnumerableExtensions.cs", 13),
         ("LiteDB.BsonDataReader..ctor()",
          "LiteDB/Document/DataReader/BsonDataReader.cs", 56),
         ("LiteDB.Engine.QueryExecutor.ExecuteQuery()",
          "LiteDB/Engine/Query/QueryExecutor.cs", 87),
         ("LiteDB.Engine.LiteEngine.Query()", "LiteDB/Engine/Engine/Query.cs", 45),
         ("LiteDB.Tests.Issues.Issue2870_Tests.FirstRead()",
          "LiteDB.Tests/Issues/Issue2870_Tests.cs", 30)],
        "ThrowAtOriginalSourceSite"),
    ISSUE_2870_PREFIX + "WaitIfLocked_preserves_the_original_frame_for_non_lock_errors": (
        [("LiteDB.IOExceptionExtensions.WaitIfLocked()",
          "LiteDB/Utils/Extensions/IOExceptionExtensions.cs", 38),
         ("LiteDB.Tests.Issues.Issue2870_Tests.WaitIfLocked()",
          "LiteDB.Tests/Issues/Issue2870_Tests.cs", 96)],
        "ThrowAtOriginalWaitSite"),
}


def stack_assertion(frames, expected_frame, checkout, include_action_frame):
    checkout_segment = f"{checkout}/" if checkout else ""
    lines = [f"   at {method} in /home/runner/work/LiteDB/LiteDB/{checkout_segment}{path}:line {line}"
             for method, path, line in frames]
    if include_action_frame:
        lines.append("   at FluentAssertions.Specialized.ActionAssertions.InvokeSubject()")
    lines.append(
        "   at FluentAssertions.Specialized.DelegateAssertions`2."
        "InvokeSubjectWithInterception()")
    return ('Expected actual.StackTrace "' + "\n".join(lines)
            + f'" to contain "{expected_frame}".')


class FailureNormalizationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.policy, cls.digest = load_failure_normalization(POLICY_PATH)

    def assert_same_failure(self, name, first, second):
        left = canonical_failure(name, first, self.policy, baseline=True)
        right = canonical_failure(name, second, self.policy, baseline=True)
        self.assertEqual(left, right)

    def test_policy_has_only_reviewed_exact_cases_and_a_content_digest(self):
        self.assertEqual(16, len(self.policy))
        self.assertEqual(64, len(self.digest))
        self.assertEqual(self.digest,
                         load_failure_normalization(POLICY_PATH)[1])

    def test_generated_object_ids_normalize_without_changing_the_assertion(self):
        prefix = 'Expected rows, but found {"ObjectId":{"$oid":"'
        suffix = '"}|"target-a"} contains 1 item(s) too many.'
        self.assert_same_failure(OBJECT_ID, prefix + "0" * 24 + suffix,
                                 prefix + "a" * 24 + suffix)

    def test_temp_paths_normalize_on_windows_and_unix(self):
        first = "IOException: file 'C:\\Users\\one\\Temp\\litedb-123ab.db' is locked."
        second = "IOException: file '/tmp/litedb-9fed0.db' is locked."
        self.assert_same_failure(TEMP_DATABASE, first, second)

    def test_repeated_temp_log_path_requires_both_diagnostics(self):
        first = ("FileNotFoundException: 'C:\\Temp\\litedb-123ab-log.db'.\n"
                 "File name: 'C:\\Temp\\litedb-123ab-log.db'")
        second = ("FileNotFoundException: '/tmp/litedb-9fed0-log.db'.\n"
                  "File name: '/tmp/litedb-9fed0-log.db'")
        self.assert_same_failure(TEMP_LOG, first, second)

    def test_generated_id_ciphertext_and_hash_diagnostics_normalize(self):
        self.assert_same_failure(
            GENERATED_ID,
            'Expected raw["_id"] to be equal to 491, but found null.',
            'Expected raw["_id"] to be equal to 1491, but found null.')
        block_a = "{" + ", ".join(f"0x{value:02X}" for value in range(16)) + "}"
        block_b = "{" + ", ".join(f"0x{value:02X}" for value in range(16, 32)) + "}"
        self.assert_same_failure(CIPHERTEXT,
                                 f"Did not expect collections {block_a} and {block_a} to be equal.",
                                 f"Did not expect collections {block_b} and {block_b} to be equal.")
        self.assert_same_failure(
            HASH_CODE,
            "Expected collation.GetHashCode(lower) to be -12, but found 19 (difference of 31).",
            "Expected collation.GetHashCode(lower) to be 44, but found -3 (difference of -47).")

    def test_foreign_file_hash_and_prefix_normalize_together(self):
        first = ('Expected Sha256(after) to be "' + "1" * 64
                 + '" because data must remain, but "' + "2" * 64
                 + '" differs near "222" (index 1).')
        second = ('Expected Sha256(after) to be "' + "1" * 64
                  + '" because data must remain, but "' + "a" * 64
                  + '" differs near "aaa" (index 1).')
        self.assert_same_failure(FOREIGN_FILE, first, second)

        different_index = second.replace("(index 1)", "(index 0)")
        self.assert_same_failure(FOREIGN_FILE, first, different_index)
        changed_expected_hash = second.replace('"' + "1" * 64 + '"',
                                               '"' + "3" * 64 + '"')
        self.assertNotEqual(canonical_failure(FOREIGN_FILE, first, self.policy),
                            canonical_failure(FOREIGN_FILE, changed_expected_hash,
                                              self.policy))

    def test_wal_peak_normalizes_only_the_observed_peak_diagnostics(self):
        prefix = ("Expected peak to be less than or equal to 6553600L because bounded growth, "
                  "but found ")
        self.assert_same_failure(WAL_PEAK, prefix + "7000000L (difference of 446400).",
                                 prefix + "8000000L (difference of 1446400).")

    def test_reviewed_stack_assertions_ignore_only_checkout_and_jit_wrapper(self):
        for name, (frames, expected_frame) in STACK_CASES.items():
            baseline = stack_assertion(frames, expected_frame, "baseline", False)
            candidate = stack_assertion(frames, expected_frame, "candidate", True)
            controller = stack_assertion(frames, expected_frame, None, False)
            with self.subTest(name=name):
                normalized = canonical_failure(name, baseline, self.policy, baseline=True)
                self.assertEqual(normalized,
                                 canonical_failure(name, candidate, self.policy))
                self.assertEqual(normalized,
                                 canonical_failure(name, controller, self.policy,
                                                   baseline=True))
                self.assertIn(f'to contain "{expected_frame}"', normalized)
                for method, path, line in frames:
                    self.assertIn(method, normalized)
                    self.assertIn(f"{path}:line {line}", normalized)
                self.assertNotIn("/baseline/", normalized)
                self.assertNotIn("ActionAssertions.InvokeSubject", normalized)

    def test_stack_assertion_method_and_expected_frame_changes_are_preserved(self):
        name = next(iter(STACK_CASES))
        frames, expected_frame = STACK_CASES[name]
        baseline = stack_assertion(frames, expected_frame, "baseline", False)
        changed_method = stack_assertion(
            [("LiteDB.BsonDataReader.Write()", *frames[0][1:]), *frames[1:]],
            expected_frame, "candidate", True)
        changed_expected = stack_assertion(
            frames, "DifferentOriginalSourceSite", "candidate", True)
        normalized = canonical_failure(name, baseline, self.policy, baseline=True)
        self.assertNotEqual(normalized,
                            canonical_failure(name, changed_method, self.policy))
        self.assertNotEqual(normalized,
                            canonical_failure(name, changed_expected, self.policy))

    def test_stack_assertion_missing_frame_fails_closed_for_baseline(self):
        name = (ISSUE_2870_PREFIX
                + "Query_preserves_the_original_frame_when_a_later_source_read_throws")
        frames, expected_frame = STACK_CASES[name]
        incomplete = stack_assertion(frames[:-1], expected_frame, "baseline", False)
        with self.assertRaisesRegex(FailureNormalizationError,
                                    "does not match reviewed normalization"):
            canonical_failure(name, incomplete, self.policy, baseline=True)

    def test_reviewed_alternate_path_count_does_not_hide_an_extra_frame(self):
        name = (ISSUE_2870_PREFIX
                + "Query_preserves_the_original_frame_when_the_first_source_read_throws")
        frames, expected_frame = STACK_CASES[name]
        ordinary = stack_assertion(frames, expected_frame, None, False)
        with_extra_frame = stack_assertion(
            [*frames, ("LiteDB.Tests.Issues.Issue2870_Tests.AdditionalFrame()",
                       "LiteDB.Tests/Issues/Issue2870_Tests.cs", 31)],
            expected_frame, None, False)
        first = canonical_failure(name, ordinary, self.policy, baseline=True)
        second = canonical_failure(name, with_extra_frame, self.policy, baseline=True)
        self.assertNotEqual(first, second)
        self.assertIn("AdditionalFrame", second)

    def test_assertion_or_exception_changes_are_preserved(self):
        first = "IOException: file 'C:\\Temp\\litedb-123ab.db' is locked."
        changed = "UnauthorizedAccessException: file 'C:\\Temp\\litedb-9fed0.db' is locked."
        self.assertNotEqual(canonical_failure(TEMP_DATABASE, first, self.policy),
                            canonical_failure(TEMP_DATABASE, changed, self.policy))

    def test_unreviewed_test_names_are_never_normalized(self):
        failure = 'Expected raw["_id"] to be equal to 491, but found null.'
        self.assertEqual(failure, canonical_failure("Some.Other.Test", failure, self.policy))

    def test_wrong_baseline_shape_fails_closed(self):
        with self.assertRaisesRegex(FailureNormalizationError,
                                    "does not match reviewed normalization"):
            canonical_failure(GENERATED_ID,
                              'Expected raw["_id"] to be equal to 491, but found 3.',
                              self.policy, baseline=True)

    def test_candidate_with_wrong_shape_retains_original_failure(self):
        failure = 'Expected raw["_id"] to be equal to 491, but found 3.'
        self.assertEqual(failure, canonical_failure(GENERATED_ID, failure, self.policy))

    def test_invalid_policy_is_rejected(self):
        invalid_counts = ([], [1, 1], [0, 1], [True], "1")
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "policy.json"
            for matches in invalid_counts:
                invalid = {"schema_version": 1, "tests": {"Exact.Test": [
                    {"pattern": "value", "replacement": "", "matches": matches}]}}
                path.write_text(json.dumps(invalid), encoding="utf-8")
                with self.subTest(matches=matches), \
                        self.assertRaises(FailureNormalizationError):
                    load_failure_normalization(path)


if __name__ == "__main__":
    unittest.main()
