# Audited source-context observation: issue 2871 / guard 132

The frozen `AuditFindingSourceGuard_Tests` row 132 tests whether an exact
three-line source string remains in `Reflection.cs`. A comment or indentation
change can make it pass without fixing constructor-cache synchronization.

Only issue 2871 declares this observation. The trusted observer verifies the
exact frozen fixture and JSON blobs, guard identity, source blob and context
hash. It requires the context to occur once in the actual immutable baseline
and disappear in the candidate's exact approved `Reflection.cs` diff.

The broad comparator still requires all frozen test identities and results.
Only the authenticated guard-132 `Failed -> Passed` transition is recorded as
`source_context_changes`; its exact original failure must also match. The
record says `behavior_unverified: true` and `issue_credit: false`. Missing,
skipped or differently failed guards remain blocking, as do every other
unexpected pass and all behavioral regressions.

The ordinary issue-2871 behavioral regression and concurrency control remain
required. Review tasks independently derive the source observation from Git,
without downloading CI artifacts. Reviewers must retain this distinction in
their coverage, and the lifecycle role must establish actual synchronization
of cache reads and writes.

Integration rechecks the immutable source observation and stores it separately
from the accepted behavioral test names. Final promotion reauthenticates the
same recorded per-fix observation and verifies the source context has not
returned in the final integration commit. It does not award an additional
issue or behavioral-test pass for the source guard.

Guard 111 and the behavioral
`M111_uint64_round_trips_through_bson_value` test have no disposition here.
Their unexpected passes continue to block until separately reviewed.

This policy is for a newly pinned future runtime. Existing campaign definitions
and completed evidence retain their original policy identities.
