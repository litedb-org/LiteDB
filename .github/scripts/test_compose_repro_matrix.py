import json
import os
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import compose_repro_matrix


ROOT = Path(__file__).resolve().parents[2]


class ReproMatrixTests(unittest.TestCase):
    def compose(self, repros, tier="full", include_retired=False, retired=None, invalid=None):
        with tempfile.TemporaryDirectory() as temporary:
            workspace = Path(temporary)
            (workspace / ".github").mkdir()
            for name in ("os-matrix.json", "repro-ci.json"):
                (workspace / ".github" / name).write_bytes((ROOT / ".github" / name).read_bytes())
            policy = json.loads((workspace / ".github/repro-ci.json").read_text()) if retired is None else retired
            (workspace / ".github/repro-ci.json").write_text(json.dumps(policy))
            for entry in policy.values():
                for reference in entry.get("tests", []):
                    path = reference.split("#")[0]
                    if (ROOT / path).exists():
                        target = workspace / path
                        target.parent.mkdir(parents=True, exist_ok=True)
                        target.write_bytes((ROOT / path).read_bytes())
            (workspace / "repros.json").write_text(json.dumps({"repros": repros, "invalid": invalid}))
            output = workspace / "output"
            summary = workspace / "summary"
            with patch.dict(os.environ, {
                "GITHUB_WORKSPACE": str(workspace), "GITHUB_OUTPUT": str(output),
                "GITHUB_STEP_SUMMARY": str(summary), "CI_TIER": tier,
                "INCLUDE_REGRESSION_REPROS": str(include_retired).lower(),
            }):
                compose_repro_matrix.main()
            values = dict(line.split("=", 1) for line in output.read_text().splitlines())
            return json.loads(values["matrix"])["include"], json.loads(values["skipped"]), summary.read_text()

    def inventory(self):
        return [dict(name=m["id"], supports=m.get("supports", ["any"]))
                for path in (ROOT / "LiteDB.ReproRunner/Repros").glob("*/repro.json")
                for m in [json.loads(path.read_text())]]

    def test_fixed_repros_are_replaced_by_real_test_methods_in_both_tiers(self):
        retired = json.loads((ROOT / ".github/repro-ci.json").read_text())
        platforms = json.loads((ROOT / ".github/os-matrix.json").read_text())
        active = [repro for repro in self.inventory() if repro["name"] not in retired]
        self.assertEqual({"Issue_2561_TransactionMonitor", "Issue_3082_AllocationPublication",
                          "Issue_3094_SharedRetryWriterRelease", "Issue_3099_TimeoutAfterWalRestore"},
                         {repro["name"] for repro in active})
        for tier in ("pr", "full"):
            with self.subTest(tier=tier):
                entries, skipped, _ = self.compose(self.inventory(), tier)
                expected = {(repro["name"], platform, label) for repro in active
                            for platform, labels in platforms.items()
                            if "any" in repro["supports"] or platform in repro["supports"]
                            for label in (labels[:1] if tier == "pr" else labels)}
                self.assertEqual(len(expected), len(entries))
                self.assertEqual(expected, {(entry["repro"], entry["platform"], entry["os"]) for entry in entries})
                # Every retired repro is reported as skipped, none silently dropped.
                self.assertEqual(len(retired), len(skipped))
                self.assertEqual(set(retired), {entry.split(" (", 1)[0] for entry in skipped})

    def test_shared_handle_inventory_is_complete(self):
        policy = json.loads((ROOT / ".github/repro-ci.json").read_text())
        expected = {'LiteDB.Tests/Internals/TransactionHandleSharedTenure_Tests.cs#Retained_pin_survives_creator_exit_and_releases_once_after_foreign_completion', 'LiteDB.Tests/Internals/TransactionHandleSharedWaitGuards_Tests.cs#Ordinary_and_legacy_waiters_timeout_without_borrowing_handle_then_progress', 'LiteDB.Tests/Engine/TransactionHandleSharedStorage_Tests.cs#Shared_caller_stream_refusal_precedes_storage_mutation', 'LiteDB.Tests/Internals/TransactionHandleSharedProcess_Tests.cs#Process_death_with_open_handle_discards_spill_and_recovers_preserved_indexes', 'LiteDB.Tests/Internals/TransactionHandleSharedProcess_Tests.cs#Turnstile_deadline_releases_partial_request_and_next_handle_progresses', 'LiteDB.Tests/Internals/TransactionHandleSharedProcess_Tests.cs#Open_handle_blocks_remote_begin_until_completion_and_timeout_does_not_release_holder', 'LiteDB.Tests/Engine/TransactionHandleSharedStorage_Tests.cs#Shared_private_storage_cannot_claim_persistent_handle_commits', 'LiteDB.Tests/Engine/TransactionHandleCompletion_Tests.cs#Completed_undisposed_handle_releases_everything_it_owned', 'LiteDB.Tests/Internals/TransactionHandleSharedWaitGuards_Tests.cs#Executing_callback_cannot_wait_for_its_own_handle_and_prior_writes_survive', 'LiteDB.Tests/Internals/TransactionHandleSharedIdentity_Tests.cs#Caller_identity_policy_precedes_admission_and_preserves_the_existing_handle', 'LiteDB.Tests/Internals/TransactionHandleReuseProcess_Tests.cs#Another_process_writes_within_its_bound_and_reused_handles_never_read_stale_state', 'LiteDB.Tests/Internals/TransactionHandleSharedAbandonment_Tests.cs#Holder_context_does_not_root_abandoned_facade_or_open_handle', 'LiteDB.Tests/Engine/TransactionHandleCloseRace_Tests.cs#Close_racing_handles_keeps_exactly_the_acknowledged_commits', 'LiteDB.Tests/Internals/TransactionHandleSharedProcess_Tests.cs#Open_handle_excludes_file_replacement_and_rekey_without_mutation', 'LiteDB.Tests/Engine/TransactionHandleCompletion_Tests.cs#Handles_work_on_memory_and_temporary_storage', 'LiteDB.Tests/Internals/TransactionHandleSharedProcess_Tests.cs#Interrupted_begin_retires_only_its_native_request_and_successor_progresses', 'LiteDB.Tests/Engine/TransactionHandleAbandonment_Tests.cs#Completed_handle_does_not_root_abandoned_session_and_active_handle', 'LiteDB.Tests/Internals/TransactionHandleSharedTenure_Tests.cs#External_engine_close_fences_late_completion_and_detaches_the_failed_handle', 'LiteDB.Tests/Internals/TransactionHandleSharedTenure_Tests.cs#Waiting_handle_times_out_without_stealing_live_generation_and_then_reuses', 'LiteDB.Tests/Engine/TransactionHandleCompletion_Tests.cs#Maintenance_respects_a_lease_taken_on_another_thread_and_runs_after_completion', 'LiteDB.Tests/Internals/TransactionHandleSharedTenure_Tests.cs#Failed_spill_discards_the_core_releases_the_handle_and_next_generation_recovers'}
        prefixes = ('LiteDB.Tests/Engine/TransactionHandleSharedStorage_Tests.cs#', 'LiteDB.Tests/Internals/TransactionHandleSharedIdentity_Tests.cs#', 'LiteDB.Tests/Internals/TransactionHandleSharedAbandonment_Tests.cs#', 'LiteDB.Tests/Engine/TransactionHandleAbandonment_Tests.cs#', 'LiteDB.Tests/Engine/TransactionHandleCloseRace_Tests.cs#', 'LiteDB.Tests/Engine/TransactionHandleCompletion_Tests.cs#', 'LiteDB.Tests/Internals/TransactionHandleReuseProcess_Tests.cs#', 'LiteDB.Tests/Internals/TransactionHandleSharedProcess_Tests.cs#', 'LiteDB.Tests/Internals/TransactionHandleSharedTenure_Tests.cs#', 'LiteDB.Tests/Internals/TransactionHandleSharedWaitGuards_Tests.cs#')
        self.assertEqual(expected, {ref for ref in policy["SharedSelfTeardownRelease"]["tests"]
                                    if ref.startswith(prefixes)})

    def test_wal_handle_replacement_inventory_is_complete(self):
        policy = json.loads((ROOT / ".github/repro-ci.json").read_text())
        expected = {
            'LiteDB.Tests/Internals/WalHandleLifetime_Tests.cs#Wal_handle_preserves_exclusivity_truncation_and_disposal',
            'LiteDB.Tests/Internals/WalHandleLifetime_Tests.cs#Wal_caller_file_stream_retains_buffering_until_flush',
            'LiteDB.Tests/Internals/WalHandleLifetime_Tests.cs#Wal_completed_native_writes_preserve_offsets_bytes_and_reopen',
            'LiteDB.Tests/Internals/WalHandleLifetime_Tests.cs#Wal_completed_native_write_reports_access_failure_without_mutating_file',
            'LiteDB.Tests/Internals/WalHandleLifetime_Tests.cs#Wal_reopens_after_checkpoint_rebuild_and_disposal',
        }
        actual = {ref for entry in policy.values() for ref in entry["tests"]
                  if ref.startswith("LiteDB.Tests/Internals/WalHandleLifetime_Tests.cs#")}
        self.assertEqual(expected, actual)

    def test_coordinated_pin_admission_inventory_is_complete(self):
        policy = json.loads((ROOT / ".github/repro-ci.json").read_text())
        self.assertEqual({
            "LiteDB.Tests/Internals/SharedWriterTenureCoordinatedAdmission_Tests.cs#Pin_handoff_after_admission_sample_reads_fresh_commits_and_preserves_old_snapshot",
        }, {ref for ref in policy["SharedSelfTeardownRelease"]["tests"]
           if ref.startswith("LiteDB.Tests/Internals/SharedWriterTenureCoordinatedAdmission_Tests.cs#")})

    def test_rekey_callback_cache_gate_inventory_is_complete(self):
        policy = json.loads((ROOT / ".github/repro-ci.json").read_text())
        expected = {
            "LiteDB.Tests/Internals/SharedOneFileRekey_Tests.cs#Legacy_rebuild_callback_refuses_peer_read_before_waiting_for_snapshot_cache",
        }
        actual = {ref for entry in policy.values() for ref in entry["tests"]
                  if "#Legacy_rebuild_callback_refuses_peer_read_before_waiting_for_snapshot_cache" in ref}
        self.assertEqual(expected, actual)

    def test_offline_upgrade_inventory_is_complete(self):
        policy = json.loads((ROOT / ".github/repro-ci.json").read_text())
        expected = {
            "LiteDB.Tests/Internals/SharedOfflineUpgrade_Tests.cs#Writable_mapped_open_retires_full_or_shm_only_legacy_state_before_storage",
            "LiteDB.Tests/Internals/SharedOfflineUpgrade_Tests.cs#Every_retirement_cut_and_interrupted_retry_preserve_database_and_wal",
            "LiteDB.Tests/Internals/SharedOfflineUpgrade_Tests.cs#Every_subset_of_resurrected_ephemeral_entries_retires_repeatably",
            "LiteDB.Tests/Internals/SharedOfflineUpgrade_Tests.cs#Refusal_preserves_every_byte_and_directory_entry",
            "LiteDB.Tests/Internals/SharedOfflineUpgrade_Tests.cs#Live_legacy_peer_refuses_then_offline_upgrade_and_new_legacy_open_are_safe",
            "LiteDB.Tests/Internals/SharedOfflineUpgrade_Tests.cs#Dms_owner_excludes_retirement_and_unknown_shm_is_preserved",
        }
        self.assertEqual(expected, {ref for ref in policy["SharedSelfTeardownRelease"]["tests"]
                                   if "/SharedOfflineUpgrade_Tests.cs#" in ref})

    def test_update_index_diff_inventory_is_complete(self):
        policy = json.loads((ROOT / ".github/repro-ci.json").read_text())
        expected = {'LiteDB.Tests/Engine/UpdateIndexDiff_Tests.cs#Multikey_updates_match_independent_model_and_reopen', 'LiteDB.Tests/Engine/UpdateIndexDiff_Tests.cs#Collation_equivalent_and_changed_unique_keys_preserve_results', 'LiteDB.Tests/Engine/UpdateIndexDiff_Tests.cs#Moving_index_keys_during_update_many_visits_each_document_once', 'LiteDB.Tests/Engine/UpdateIndexDiff_Tests.cs#Unique_failure_rolls_back_data_and_all_index_changes', 'LiteDB.Tests/Engine/UpdateIndexDiff_Tests.cs#Secondary_key_changes_coexist_with_vector_updates_and_reopen'}
        actual = {ref for entry in policy.values() for ref in entry["tests"]
                  if ref.startswith("LiteDB.Tests/Engine/UpdateIndexDiff_Tests.cs#")}
        self.assertEqual(expected, actual)

    def test_primary_key_delete_inventory_is_complete(self):
        policy = json.loads((ROOT / ".github/repro-ci.json").read_text())
        expected = {
            'LiteDB.Tests/Engine/PrimaryKeyDeleteRange_Tests.cs#Range_matches_old_projection_path_across_pages_rollback_and_reopen',
            'LiteDB.Tests/Engine/PrimaryKeyDeleteRange_Tests.cs#String_bounds_use_execution_collation',
            'LiteDB.Tests/Engine/PrimaryKeyDeleteRange_Tests.cs#Live_readers_keep_their_snapshot_during_delete_and_page_reuse',
            'LiteDB.Tests/Engine/PrimaryKeyDeleteRange_Tests.cs#Unproven_shapes_use_existing_path',
            'LiteDB.Tests/Engine/PrimaryKeyDeleteFault_Tests.cs#Every_delete_wal_write_failure_recovers_atomic_rows_and_indexes',
            'LiteDB.Tests/Engine/PrimaryKeyDeleteFault_Tests.cs#Interrupted_confirmation_recovers_whole_delete_on_repeated_reopen',
            'LiteDB.Tests/Engine/PrimaryKeyDeleteRange_Tests.cs#Read_transform_retains_query_callback_semantics',
            'LiteDB.Tests/Engine/PrimaryKeyDeleteRange_Tests.cs#Same_thread_reader_retains_pre_write_snapshot',
        }
        actual = {ref for entry in policy.values() for ref in entry["tests"]
                  if ref.startswith("LiteDB.Tests/Engine/PrimaryKeyDelete")}
        self.assertEqual(expected, actual)
    def test_mapper_capacity_inventory_is_complete(self):
        policy = json.loads((ROOT / ".github/mapper-repro-ci.json").read_text())
        expected = {
            'LiteDB.Tests/Mapper/MapperDocumentCapacity_Tests.cs#Mutable_members_omission_and_order_match_original_pipeline',
            'LiteDB.Tests/Mapper/MapperDocumentCapacity_Tests.cs#Custom_serializers_virtual_hooks_and_polymorphism_match_original_pipeline',
            'LiteDB.Tests/Mapper/MapperDocumentCapacity_Tests.cs#Dictionaries_and_metadata_match_original_pipeline',
            'LiteDB.Tests/Mapper/MapperDocumentCapacity_Tests.cs#Getter_failure_keeps_member_diagnostics',
            'LiteDB.Tests/Mapper/MapperDocumentCapacity_Tests.cs#MapperCapacityHints_CustomSerializerIgnoredAndDuplicateFields_MatchBaseline',
            'LiteDB.Tests/Mapper/MapperDocumentCapacity_Tests.cs#MapperCapacityHints_ThrowingCallbackAndRollback_PreserveAllIndexes',
            'LiteDB.Tests/Mapper/MapperDocumentCapacity_Tests.cs#Mapped_bulk_documents_and_indexes_survive_rollback_and_reopen',
        }
        actual = {ref for entry in policy.values() for ref in entry["tests"]
                  if ref.startswith("LiteDB.Tests/Mapper/MapperDocumentCapacity_Tests.cs#")}
        self.assertEqual(expected, actual)
        for reference in expected:
            path, method = reference.split("#")
            self.assertIn("public void " + method + "(", (ROOT / path).read_text())

    def test_manual_override_keeps_historical_reproductions_available(self):
        entries, skipped, _ = self.compose(self.inventory(), include_retired=True)
        platforms = json.loads((ROOT / ".github/os-matrix.json").read_text())
        expected = {(repro["name"], label) for repro in self.inventory()
                    for platform, labels in platforms.items()
                    if "any" in repro["supports"] or platform in repro["supports"]
                    for label in labels}
        self.assertEqual(expected, {(entry["repro"], entry["os"]) for entry in entries})
        self.assertEqual(len(expected), len(entries))
        self.assertEqual([], skipped)

    def test_new_repros_are_not_silently_retired(self):
        entries, _, _ = self.compose([{"name": "NewSafetyCheck"}])
        self.assertEqual(3, len(entries))

    def test_missing_replacement_test_blocks_retirement(self):
        with self.assertRaisesRegex((ValueError, FileNotFoundError), "missing"):
            self.compose([], retired={"Fixed": {"reason": "covered", "tests": ["LiteDB.Tests/missing.cs#Missing"]}})

    def test_retirement_requires_reason_and_tests(self):
        with self.assertRaisesRegex(ValueError, "needs a reason and replacement tests"):
            self.compose([], retired={"Fixed": {"tests": []}})

    def test_malformed_and_substring_references_cannot_retire_a_repro(self):
        source = "LiteDB.Tests/Issues/Issue2614_InitializationCleanup_Tests.cs"
        for reference in [source, source + "#", source + "#ReleasesFileHandle_AndAllowsRetry"]:
            with self.subTest(reference=reference):
                with self.assertRaisesRegex(ValueError, "Missing replacement test for Fixed:"):
                    self.compose([], retired={"Fixed": {"reason": "covered", "tests": [reference]}})

    def test_invalid_manifests_and_tiers_fail_closed(self):
        with self.assertRaisesRegex(ValueError, "Invalid repro manifests"):
            self.compose([], invalid=[{"name": "broken", "errors": ["bad manifest"]}])
        with self.assertRaisesRegex(ValueError, "Unsupported CI tier"):
            self.compose([], tier="typo")

    def test_pr_selection_respects_pinned_and_excluded_runner_labels(self):
        for constraints in [{"includeLabels": ["ubuntu-24.04"]}, {"excludeLabels": ["ubuntu-22.04"]}]:
            with self.subTest(constraints=constraints):
                entries, skipped, summary = self.compose([
                    {"name": "Pinned", "supports": ["linux"], "os": constraints}
                ], tier="pr")
                self.assertEqual(["ubuntu-24.04"], [e["os"] for e in entries])
                self.assertEqual([], skipped)
                self.assertNotIn("Unknown", summary)

    def test_constraints_never_expand_platform_support(self):
        entries, skipped, _ = self.compose([
            {"name": "Linux", "supports": ["linux"], "os": {"includePlatforms": ["windows"]}}
        ])
        self.assertEqual([], entries)
        self.assertEqual(1, len(skipped))

    def test_empty_filtered_inventory_produces_no_jobs(self):
        entries, skipped, _ = self.compose([])
        self.assertEqual([], entries)
        self.assertEqual([], skipped)


if __name__ == "__main__":
    unittest.main()
