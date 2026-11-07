# Synchronous engine context port (P4 step 2)

Base: ed545c7e164ed664dfc6d65cfdedfe7e2207eee5. Mechanism source:
#3117 d877ee3d58b6e09bb1557ff34f4c235a18532028. Progress/interleaving
obligations inspected at a41df8463b56d6129b2e1a8766b7326d99a4c1ba.
This internal seam introduces no public transaction API or decorator capability.

| Source obligation | Destination | Disposition and invariant |
| --- | --- | --- |
| TransactionHandleDispatchCompleteness: Every_public_engine_member_is_classified | same suite/test | Ported; includes target GetCollectionNames guard. Every reflected instance member has an executable invoker. |
| Raw callback dispatch refusal | same suite: Every_classified_entry_refuses_raw_handle_callback_reentry, Every_classified_entry_refuses_raw_read_transform_reentry | Execute all classified invokers in synchronous callback frames; no ticket survives the first dispatch. |
| TransactionHandleHandoffProgress: Peer_successes_and_own_retries_do_not_renew_a_stalled_worker | same suite/test | Ported unchanged. |
| TransactionHandleHandoffProgress: Monitoring_detects_an_operation_blocked_inside_Run | same suite/test | Ported unchanged. |
| TransactionHandleHandoffProgress: Successful_work_renews_only_its_progress_budget_not_the_hard_cap | same suite/test | Ported unchanged. |
| TransactionHandleHandoffProgress: Completed_worker_cannot_hide_an_unstarted_peer | same suite/test | Ported unchanged. |
| TransactionHandleInterleaving: Forced_concurrent_histories_preserve_transaction_contracts | TransactionHandleInterleaving: Forced_histories_preserve_independent_handles_and_reopen | Engine-level Direct subset: plaintext/encrypted, commit/rollback, forced collection waiter, independent indexed/reopened oracle. Public binding and Shared explorer schedules remain for steps 3–8. |
| TransactionHandleMaintenanceProgress: Maintenance_progresses_under_sustained_handle_callback_reads | TransactionHandleMaintenanceProgress: Queued_maintenance_precedes_later_handle_admission_after_foreign_completion | Engine-level priority/handoff subset. Public bound callbacks, sustained workers and Shared lifecycle remain unqualified until later steps; no source coverage removed. |
| Raw/decorated engine capability boundary | TransactionHandleProcessHost | New: real forwarding decorator policy executes; raw reentry refuses; ordinary wrapper masks binding through dispatch and reader operations; rollback preserves independent automatic commit. No arbitrary decorator unwrapping. |
| Current group-ticket integration (source completion code replaced) | Internals/TransactionHandleGroupCommit | New: 3 forced schedules for leader/follower, zero-frame dependency, durable prefix, sync failure host stop and two indexed reopens. Retains target TransactionPages.CommitTicket and existing snapshot-release-before-ticket-wait order. |

The normal engine/remaining CI partitions discover every added suite automatically.
The targeted safety workflow adds a TransactionHandle partition on Linux, Windows
and macOS for .NET 8/10, retaining all prior partitions. Framework runs the portable
engine cases; native group-sync cases retain the existing modern-runtime boundary.

Fault model: injected device-sync exceptions and graceful repeated recovery here;
existing WAL/power-loss suites run through targeted safety CI. This does not claim
new handle public API, Shared tenure/pin behavior, multi-stage deadlines, close,
process-death or modeled-power-loss campaign qualification for the integrated API.
Exact-head hosted evidence is recorded in the step log under /tmp/litedb-proto/phases.
