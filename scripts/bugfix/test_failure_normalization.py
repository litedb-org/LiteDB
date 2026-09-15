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
FIRST_SOURCE = (ISSUE_2870_PREFIX
                + "Query_preserves_the_original_frame_when_the_first_source_read_throws")
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
    FIRST_SOURCE: (
        [("LiteDB.Engine.QueryExecutor.<>c__DisplayClass12_0."
          "<<ExecuteQuery>g__RunQuery|2>d.MoveNext()",
          "LiteDB/Engine/Query/QueryExecutor.cs", 139),
         ("LiteDB.Utils.Extensions.EnumerableExtensions.OnDispose[T]"
          "(IEnumerable`1 source, Action onDispose)+MoveNext()",
          "LiteDB/Utils/Extensions/EnumerableExtensions.cs", 13),
         ("LiteDB.Utils.Extensions.EnumerableExtensions.OnDispose[T]"
          "(IEnumerable`1 source, Action onDispose)+MoveNext()",
          "LiteDB/Utils/Extensions/EnumerableExtensions.cs", 13),
         ("LiteDB.BsonDataReader..ctor"
          "(IEnumerable`1 values, String collection, EngineState state)",
          "LiteDB/Document/DataReader/BsonDataReader.cs", 56),
         ("LiteDB.Engine.QueryExecutor.ExecuteQuery(Boolean executionPlan)",
          "LiteDB/Engine/Query/QueryExecutor.cs", 87),
         ("LiteDB.Engine.LiteEngine.Query(String collection, Query query)",
          "LiteDB/Engine/Engine/Query.cs", 45),
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


def stack_assertion(frames, expected_frame, checkout, include_action_frame,
                    include_query_wrapper=False, newline="\n", path_separator="/"):
    checkout_segment = f"{checkout}/" if checkout else ""
    lines = [f"   at {method} in /home/runner/work/LiteDB/LiteDB/{checkout_segment}"
             f"{path.replace('/', path_separator)}:line {line}"
             for method, path, line in frames]
    if include_query_wrapper:
        boolean_frame = next(
            index for index, (method, _, _) in enumerate(frames)
            if method == "LiteDB.Engine.QueryExecutor.ExecuteQuery(Boolean executionPlan)")
        lines.insert(
            boolean_frame + 1,
            "   at LiteDB.Engine.QueryExecutor.ExecuteQuery() in "
            f"/home/runner/work/LiteDB/LiteDB/{checkout_segment}"
            + "LiteDB/Engine/Query/QueryExecutor.cs".replace("/", path_separator)
            + ":line 59")
    if include_action_frame:
        lines.append("   at FluentAssertions.Specialized.ActionAssertions.InvokeSubject()")
    lines.append(
        "   at FluentAssertions.Specialized.DelegateAssertions`2."
        "InvokeSubjectWithInterception()")
    return ('Expected actual.StackTrace "' + newline.join(lines)
            + f'" to contain "{expected_frame}".')


def first_source_assertion(variant, checkout, include_wrapper_frames=False,
                           newline="\n", path_separator="/"):
    frames, expected_frame = STACK_CASES[FIRST_SOURCE]
    failure = stack_assertion(
        frames, expected_frame, checkout, include_wrapper_frames,
        include_query_wrapper=include_wrapper_frames, newline=newline,
        path_separator=path_separator)
    lines = failure.split(newline)
    on_dispose = [index for index, line in enumerate(lines)
                  if "EnumerableExtensions.OnDispose[T]" in line]
    if variant == "one-unlocated-line56":
        lines.pop(on_dispose[1])
        lines[on_dispose[0]] = lines[on_dispose[0]].split(" in ", 1)[0]
    elif variant == "one-line13-line46":
        lines.pop(on_dispose[1])
        bson = next(index for index, line in enumerate(lines)
                    if "BsonDataReader..ctor" in line)
        lines[bson] = lines[bson].replace(":line 56", ":line 46")
    elif variant == "two-first-unlocated-line56":
        lines[on_dispose[0]] = lines[on_dispose[0]].split(" in ", 1)[0]
    elif variant == "two-second-unlocated-line56":
        lines[on_dispose[1]] = lines[on_dispose[1]].split(" in ", 1)[0]
    elif variant != "two-line13-line56":
        raise ValueError(f"Unknown first-source stack variant: {variant}")
    return newline.join(lines)


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

    def test_reviewed_stack_assertions_normalize_only_reviewed_runtime_variance(self):
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
                    volatile_sequence_point = (name == FIRST_SOURCE
                                               and ("OnDispose[T]" in method
                                                    or "BsonDataReader..ctor" in method))
                    if not volatile_sequence_point:
                        self.assertIn(f"{path}:line {line}", normalized)
                self.assertNotIn("/baseline/", normalized)
                self.assertNotIn("ActionAssertions.InvokeSubject", normalized)

    def test_first_source_exact_optional_wrapper_frames_normalize_for_lf_and_crlf(self):
        _, expected_frame = STACK_CASES[FIRST_SOURCE]
        without_wrappers = first_source_assertion(
            "two-line13-line56", "baseline", newline="\r\n")
        with_wrappers = first_source_assertion(
            "two-line13-line56", "candidate", include_wrapper_frames=True,
            newline="\r\n")

        normalized = canonical_failure(
            FIRST_SOURCE, without_wrappers, self.policy, baseline=True)
        self.assertEqual(
            normalized,
            canonical_failure(FIRST_SOURCE, with_wrappers, self.policy))
        self.assertIn("ExecuteQuery(Boolean executionPlan)", normalized)
        self.assertIn("LiteEngine.Query(String collection, Query query)", normalized)
        self.assertIn('to contain "ThrowAtOriginalSourceSite"', normalized)
        self.assertNotIn("QueryExecutor.ExecuteQuery()", normalized)
        self.assertNotIn("ActionAssertions.InvokeSubject", normalized)

        windows_baseline = first_source_assertion(
            "two-line13-line56", "baseline", newline="\r\n",
            path_separator="\\")
        windows_candidate = first_source_assertion(
            "two-line13-line56", "candidate", include_wrapper_frames=True,
            newline="\r\n", path_separator="\\")
        self.assertEqual(
            canonical_failure(
                FIRST_SOURCE, windows_baseline, self.policy, baseline=True),
            canonical_failure(FIRST_SOURCE, windows_candidate, self.policy))

    def test_first_source_exact_runtime_sequence_variants_normalize(self):
        canonical = canonical_failure(
            FIRST_SOURCE,
            first_source_assertion("two-line13-line56", "baseline"),
            self.policy,
            baseline=True)
        for variant in ("one-unlocated-line56", "one-line13-line46",
                        "two-line13-line56", "two-first-unlocated-line56",
                        "two-second-unlocated-line56"):
            with self.subTest(variant=variant):
                normalized = canonical_failure(
                    FIRST_SOURCE,
                    first_source_assertion(variant, "candidate"),
                    self.policy)
                self.assertEqual(canonical, normalized)
                self.assertEqual(2, normalized.count(
                    "EnumerableExtensions.OnDispose[T]"))
                self.assertIn("BsonDataReader..ctor", normalized)
                self.assertIn("RunQuery|2>d.MoveNext()", normalized)

    def test_first_source_runtime_sequence_rejects_unreviewed_changes(self):
        canonical = canonical_failure(
            FIRST_SOURCE,
            first_source_assertion("two-line13-line56", "baseline"),
            self.policy,
            baseline=True)
        ordinary = first_source_assertion(
            "two-line13-line56", "candidate")
        on_dispose = next(line for line in ordinary.splitlines()
                          if "EnumerableExtensions.OnDispose[T]" in line)
        changed = (
            ordinary.replace(on_dispose + "\n",
                             on_dispose + "\n" + on_dispose + "\n", 1),
            ordinary.replace("EnumerableExtensions.cs:line 13",
                             "EnumerableExtensions.cs:line 14", 1),
            ordinary.replace("BsonDataReader.cs:line 56",
                             "BsonDataReader.cs:line 55"),
            ordinary.replace("EnumerableExtensions.OnDispose[T]",
                             "EnumerableExtensions.OtherIterator[T]", 1),
        )
        for failure in changed:
            with self.subTest(failure=failure):
                self.assertNotEqual(
                    canonical,
                    canonical_failure(FIRST_SOURCE, failure, self.policy))

    def test_first_source_optional_wrapper_requires_exact_signature_source_line_and_placement(self):
        frames, expected_frame = STACK_CASES[FIRST_SOURCE]
        baseline = stack_assertion(frames, expected_frame, "baseline", False)
        candidate = stack_assertion(
            frames, expected_frame, "candidate", True,
            include_query_wrapper=True)
        normalized = canonical_failure(
            FIRST_SOURCE, baseline, self.policy, baseline=True)

        mutations = (
            candidate.replace("QueryExecutor.ExecuteQuery() in",
                              "QueryExecutor.ExecuteQuery(Int32 value) in"),
            candidate.replace("QueryExecutor.cs:line 59",
                              "QueryExecutor.cs:line 60", 1),
            candidate.replace("LiteDB/Engine/Query/QueryExecutor.cs:line 59",
                              "LiteDB/Engine/Engine/Query.cs:line 59"),
            candidate.replace("ExecuteQuery(Boolean executionPlan)",
                              "ExecuteQuery(Boolean changedPlan)"),
            candidate.replace("LiteEngine.Query(String collection, Query query)",
                              "LiteEngine.Query(String changed, Query query)"),
        )
        for changed in mutations:
            with self.subTest(changed=changed):
                self.assertNotEqual(
                    normalized,
                    canonical_failure(FIRST_SOURCE, changed, self.policy))

        candidate_lines = candidate.splitlines()
        wrapper_index = next(index for index, line in enumerate(candidate_lines)
                             if "QueryExecutor.ExecuteQuery()" in line)
        wrapper = candidate_lines.pop(wrapper_index)
        lite_engine_index = next(index for index, line in enumerate(candidate_lines)
                                 if "LiteEngine.Query(String collection, Query query)" in line)
        candidate_lines.insert(lite_engine_index + 1, wrapper)
        wrong_placement = "\n".join(candidate_lines)
        self.assertNotEqual(
            normalized,
            canonical_failure(FIRST_SOURCE, wrong_placement, self.policy))

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
