# Native admission safety evidence

This is a bounded application of [#3034](https://github.com/litedb-org/LiteDB/issues/3034)
to [#3046](https://github.com/litedb-org/LiteDB/pull/3046). The protocol and platform
contract remain in [native database admission](native-database-admission.md).

## Rules and discriminating scenarios

| Rule | Permitted outcome and execution proof | Executable evidence |
| --- | --- | --- |
| ADMISSION-1: one physical database has one active storage/coordination identity | Compatible same-path owners make progress. An alternate canonical bind path is rejected before data/WAL mutation, both locally and in another process. | `NativeAdmissionValidation`: real directory bind mounts, same-path positive controls, exact data/WAL bytes, production assembly with hooks absent; `NativeAdmissionPathProof_Tests`: four domains, both endpoints, distances 1/2^32/2^59, forced overlapping differing claims. |
| ADMISSION-2: ownership ends exactly at the final effective release | A returned reference keeps the kernel lock despite another owner's disposal; failed publication keeps no phantom reference. Ordinary buffer finalizers still observe native exclusion. | `NativeAdmissionOwnershipProof_Tests`: pause at actual reference publication while foreign-thread disposal starts; success/failure × Direct/Shared; raw descriptor probe before/after release. `NativeAdmissionFinalizer_Tests`: independent descriptor inside the ordinary finalizer and successful admission after finalization. |
| ADMISSION-3: replacement protects both generations | Before marker creation and throughout publication, both distinct inodes have native admission. Afterwards only the live inode remains held; final release unlocks it. | `NativeAdmissionHandoffProof_Tests`: four reached boundaries × Direct/Shared × plain/encrypted; parent probes real child-owned source/candidate handles, asserts inode change and byte preservation, then cold logical verification. Existing rollback/failure/crash suites remain enabled. |
| ADMISSION-4: admission changes do not lose acknowledged effects or expose aborted effects | Committed update/delete/insert/FileStorage effects survive two actual rebuilds, owner turnover and two cold processes; aborted effects remain absent; unrelated rows and unique indexes survive. | Production `NativeAdmissionValidation`: explicit independent expected rows/blob/metadata, index-plan assertion, uniqueness violation control, read-only-first Shared owner retained through replacement, fresh-process checks and a subsequent write/checkpoint/reopen. |
| ADMISSION-5: refusal is bounded by its contract | A conflict/unsupported environment does not mutate storage; after owner death or final release supported opens succeed. Incomplete replacement remains guarded with recovery evidence intact. | Existing native process, crash, aliases, permission, unsupported-volume, failed-downgrade and Shared-lifetime suites. Five real Shared processes, repeated kills, last-reference checks, repeated byte-preserving refused opens and complete recovery-candidate checks. |

These tests retain positive controls: rejecting every connection, omitting a
rebuild, returning an empty result or never releasing admission does not pass.
The handoff test checks distinct physical identities, not just callback names.
The path tests exercise 64-bit range lengths and skip-empty semantics explicitly.

## Historical failure and oracle checks

The inspected baseline was `b3e03caef74741da2b1370041a86359c4ec2d1cf`.
Three independent aspect reviews examined ownership, replacement and native
identity; their agreement was not counted as experimental evidence.

The native-identity review reproduced an actual lost acknowledged insert through
two directory bind mounts on that revision: both Shared transactions overlapped,
both commits returned, but cold state omitted one committed row. A same-path
control serialized the writers. The production validation scenario also fails
on a production build of that exact revision with `UNSAFE_ALIAS_ADMITTED`, while
the fixed build passes the identical scenario. This historical PR commit is used
because the new native protocol had not yet reached `dev` or a published package.

Separate deliberately unsafe variants validate the oracles; they are not claimed
as historical bugs:

| Unsafe variant | Required discriminating failure |
| --- | --- |
| Enforce path equality only in the local registry; omit OS path claims | Same-process alias rejection passes, but the child-process alias probe reports `UNSAFE_ALIAS_ADMITTED`. |
| Close the native handle on nonfinal release while leaving the registry populated | Raw ownership assertion fails while a published reference survives, for Direct and Shared. |
| Change critical admission finalization to ordinary finalization | Ordinary buffered-owner probe observes the missing native lock, for both allocation orders. |
| Omit candidate admission | All sixteen handoff cases fail the candidate's raw lock assertion. |
| Release the source immediately after candidate publication | Eight postpublication cases fail the source's raw lock assertion; earlier boundary cases still pass. |

Each mutation campaign runs a passing control, the unsafe variant with a required
named failure (not timeout/build failure), and a restored passing control. Logs,
TRX results, source hashes and exact commands are retained with the task evidence.
The original finalizer test's local registry rejection was demonstrated to pass
even after intentionally closing the native handle; the strengthened probe closes
that specific blind spot. No tests, timeouts or existing failure cases were removed.

## Repeatable execution and finite gate

```sh
dotnet build tools/NativeAdmissionValidation -c Release -p:TestingEnabled=false
dotnet build LiteDB.sln -c Release -p:TestingEnabled=true
dotnet test LiteDB.Tests -c Release -f net8.0 -p:TestingEnabled=true --no-build --settings tests.runsettings --filter 'FullyQualifiedName~NativeAdmission'
bash scripts/test-native-admission-glibc.sh
```

The last command builds the glibc 2.31 container, verifies runtime/architecture,
runs admission/rebuild suites and executes the production bind-mount scenario.
It needs Docker, not a privileged container or nested VM. Full CI runs it on
standard Ubuntu x64/ARM64 runners. Native unit/process tests also execute in the
existing Windows and macOS matrix. Production validation outputs are packaged
before test-hook assemblies are restored, and the runner rejects hook-enabled DLLs.

The finite gate is: the five rules above, their selected scenarios and unsafe
variants, then full CI/fuzz/compatibility on the final PR head. The PR description
records that exact SHA and run links; an earlier green head does not satisfy it.
Revalidate after a relevant base/head change. Historical bad-state comparison is
one-time evidence; the regression scenario remains in CI. Mutation runs remain
bounded local audit tools rather than adding five extra builds to every PR.

Limits: native queries establish exclusion at the observed boundaries, not every
possible schedule. The fault models are API/I/O exceptions, process death and
managed finalization; no hardware power-loss claim is made. Repeated marker refusal
is not described as execution of interrupted automatic recovery. The existing
marker protocol requires a complete matching recovery bundle. Fingerprint
collision resistance, qualified filesystems, stable namespace during use and a
shared named-mutex namespace remain assumptions. File-only bind mounts and moving
only the data file cannot establish historical WAL pairing. Separate isolated
container mutex namespaces require a different operation-coordination contract.
