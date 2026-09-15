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


class FailureNormalizationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.policy, cls.digest = load_failure_normalization(POLICY_PATH)

    def assert_same_failure(self, name, first, second):
        left = canonical_failure(name, first, self.policy, baseline=True)
        right = canonical_failure(name, second, self.policy, baseline=True)
        self.assertEqual(left, right)

    def test_policy_has_only_reviewed_exact_cases_and_a_content_digest(self):
        self.assertEqual(12, len(self.policy))
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
                 + '" differs near "222" (index 0).')
        second = ('Expected Sha256(after) to be "' + "1" * 64
                  + '" because data must remain, but "' + "a" * 64
                  + '" differs near "aaa" (index 0).')
        self.assert_same_failure(FOREIGN_FILE, first, second)

    def test_wal_peak_normalizes_only_the_observed_peak_diagnostics(self):
        prefix = ("Expected peak to be less than or equal to 6553600L because bounded growth, "
                  "but found ")
        self.assert_same_failure(WAL_PEAK, prefix + "7000000L (difference of 446400).",
                                 prefix + "8000000L (difference of 1446400).")

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
        invalid = {"schema_version": 1, "tests": {"Exact.Test": [
            {"pattern": ".*", "replacement": "", "matches": 1}]}}
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "policy.json"
            path.write_text(json.dumps(invalid), encoding="utf-8")
            with self.assertRaises(FailureNormalizationError):
                load_failure_normalization(path)


if __name__ == "__main__":
    unittest.main()
