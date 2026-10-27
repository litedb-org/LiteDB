# WAL padding corpus compatibility decision

The padding optimization in [525b34397](https://github.com/litedb-org/LiteDB/commit/525b34397624531d80c0aaf72ab17b449a0fd651)
intentionally omits `SetLength` when the WAL already has the required aligned
length. Accept the resulting crash-sampling change by re-pinning the input and
trace hashes of `checksum-crash:4058733:111`, `checksum-crash:3163459:201` and
`compact-crash:2937:64`. Keep their seeds, counts, assertions and historical
hashes. This changes a fuzz-generation contract, not a persisted-file format or
an acknowledgement/recovery guarantee.

## What the hashes cover

[Program.RunAsync](../LiteDB.Fuzz/Program.cs) runs the entire target and checks
latched oracle failures before checking `ExpectedInputHash`, then
`ExpectedTraceHash`. A `CORPUS_INPUT_CONTRACT_DRIFT` at that point means the target
returned without an oracle failure; it does not establish a storage defect.

[FuzzInputRandom](../LiteDB.Fuzz/FuzzInputRandom.cs) uses a seeded xorshift32
generator. Each consumed word is recorded with `BinaryWriter.Write(uint)` as
four little-endian bytes in `input.bin`. `Hash()` computes SHA-256 over that
whole file, not database/WAL bytes, the seed, or the selected crash image.
`Next(maxValue)` consumes one word when maxValue is positive, and `NextBytes`
consumes one word per output byte. Recorded-input replay instead reads existing
words and hashes the copied input file. A seed-generation replay is needed to
verify how many words the current implementation consumes.

`FuzzContext.TraceHash()` hashes `trace.jsonl`, whose writer explicitly uses LF
on every OS. These targets record scenario settings, selected event, tear prefix,
durable/volatile choice and event count. Random encryption IVs, salts and probe
file bytes are not included in either hash.

## Why padding changes the generated input

Both targets create caller-supplied memory streams and
[ChecksumCrashDevice](../LiteDB.Fuzz/Targets/ChecksumCrashDevice.cs), optionally
through encryption. They cannot satisfy the exact engine-owned `FileStream`
check in `DiskService.WriteLogPage`; the combined final-frame write is not the
cause of this drift.

The same optimization also changes [WalPadding.Pad](../LiteDB/Engine/Disk/WalPadding.cs)
from unconditional `SetLength(alignedLength)` to calling it only when the length
differs. `ChecksummedWalStream.Flush`/`FlushToDisk` and header-journal publication
call padding. A same-length call used to reach `DeviceStream.SetLength`, which
samples `before-truncate` and `after-truncate` even though no bytes change.
Omitting that call removes both events. `Capture` consumes a random word for
its reservoir-sampling decision at every armed event, and more words when it
selects an image. Scenario generation and repeated recovery share that random
stream. Changed event counts therefore change consumption, subsequent scenarios,
selected crash images and traces.

Initialization of requested padding zeros still runs at equal length. Required
length changes, writes and durable barriers remain. This disposition does not
remove any actual persistent transition or permit a weaker recovery result.

## Discriminating evidence

Compare the Windows checksum artifacts from
[before padding, run 40458632207](https://github.com/litedb-org/LiteDB/actions/runs/40458632207)
(PR head `6ac1102871a6ec9bf2695a95fe940f439b6d1f10`) with
[after padding, job 122079257625](https://github.com/litedb-org/LiteDB/actions/runs/40471467371/job/122079257625)
(PR head `f11bc0b3b7ebb19e4baf243e6031d617501628ba`). Artifact `gitSha` values
are the corresponding PR merge refs, `2857d06bb7be9c414b1a69d7a3dd27f63e3d8976`
and `f3f281e1312d3039c4d146dc1ece66f2f96fdc8d`; they are not the head SHAs.
All three before-padding cases passed their original pins. All three after-padding
cases completed every target assertion and failed only the input comparison.
The input and trace SHA-256 values below were independently recomputed from the
uploaded raw files. Each new `input.bin` is an exact shorter prefix of the old
file, consistent with unchanged xorshift words and changed consumption.

| Target / seed / count | Input bytes before → after | First scenario event count before → after | Step 2 input offset before → after |
| --- | ---: | ---: | ---: |
| checksum-crash / 4058733 / 111 | 130828 → 113868 | 141 → 115 | 1380 → 1148 |
| checksum-crash / 3163459 / 201 | 222996 → 210208 | 40 → 38 | 604 → 564 |
| compact-crash / 2937 / 64 | 24556 → 23888 | 90 → 80 | 396 → 356 |

The second checksum seed already loses exactly the two redundant truncate events
in scenario 1. The compact seed changes its selected event from
`log.before-truncate` to `log.flush`. These are sampling changes from the first
scenario, not Windows file persistence timing.

| Target / seed | New input SHA-256 | New trace SHA-256 |
| --- | --- | --- |
| checksum-crash / 4058733 | `0530C40379A2A21E5FB4C49B8EB705CDC9ABFBE57D702C5732E1F5D4BB6130B9` | `7D8C607DA6CAA9CCF56EAB76E43E941635E8D0FE13486230A79F0D074879F44C` |
| checksum-crash / 3163459 | `5B7286BB4816D39C5B3FD7177B95D21819513F9F25569CA8DD0629288FF5B083` | `748A958FA18BDB3FB6E96E583511E7D8AC5513FBDDAD368D55B73CCD119CF07F` |
| compact-crash / 2937 | `6231333351378CBB54F5BFE9BD53F55649C2A91C93AB1CBC99F7B5EF46D1193B` | `CE2D6A392DB9D85C6CA68CC83FF3E36F464162BD20C9329C77A8BE4BAAD6F4EC` |

The immediately preceding input/trace pairs and all older pairs remain in
`previousHashes` in [regressions.json](../LiteDB.Fuzz/Corpus/regressions.json).
Each changed case has its own `intentional-change` disposition in the
[coverage ledger](../.github/safety/coverage-ledger.json), linked to this decision.

## Safety oracles and limits

The unchanged [checksum target](../LiteDB.Fuzz/Targets/ChecksumCrashFuzzer.cs)
checks complete acknowledged document payloads, exact secondary-index results,
an unrelated collection, read-only byte preservation, writable recovery/checkpoint,
independent data-page CRC32C/coverage/page identity, and a second interrupted
recovery followed by verification. The unchanged
[compact target](../LiteDB.Fuzz/Targets/CompactCrashFuzzer.cs) permits only the
whole original or whole new transaction before acknowledgement and requires the
whole committed transaction after acknowledgement. It checks every payload and
index lookup, stable transaction membership through checkpoint, the unrelated
previously acknowledged WAL document and read-only byte preservation.

The deterministic `ConversionIntentRecovery_Tests.InterruptedIntentRecovery_DoesNotRewriteHeaderBeforeSealedRedo`
keeps the original checksum failure boundary covered independently of sampling.
`WalCombinedFallback_Tests.AlignedPadding_SkipsSetLengthButStillInitializesRequestedZeros`
checks the intentional equal-length behavior and required zero initialization.
The existing WalCombined file-backed tests cover physical combined writes,
sector loss/tears, earlier-frame loss, write failure, fallbacks and repeated
interrupted recovery; these memory-device corpus cases do not replace them.

A single three-entry Linux x64 .NET 10 seed-generation replay passed all 376
scenarios with both new input and trace pins enforced. Its independently generated
hashes match the uploaded Windows .NET 10 hashes exactly. This establishes the
re-pinned oracles and cross-OS generation for these inputs. In run 40471467371,
Linux/.NET 8 Checksums remained queued at triage; there is no macOS Checksums
job in the workflow. The successful macOS storage/vector smoke job does not run
these targets and is not evidence for their pins. Fresh hosted results remain
required after the re-pin.

These are bounded modeled-power-loss tests with in-memory durable/volatile
images, including torn writes and recovery retries. They assume the existing
checksum model and completed durable barriers. They are not actual process-kill,
VM/device reset, arbitrary reordering or CRC-collision evidence. The exact old
sampled scenarios are not all retained by changing pins; their hashes remain
historical provenance, while the deterministic guards retain the original
failure invariant. No target, count, oracle or expected-failure rule changes.
