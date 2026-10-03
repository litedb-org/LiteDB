# Lens subagent ({LENS})

You review a planned change to LiteDB from one angle: **{LENS}**. You have the request and
the repository at the base revision. You do NOT have the design or the implementer's plan;
do not ask for them. Read the code the request will touch and its callers, the relevant
`docs/`, `docs/rules/storage-ownership.md`, `LiteDB.Fuzz/Corpus/regressions.json`, the
known findings (`*KnownFinding*`, `ExplorerKnownFindings`, `docs/teardown-sweep.md`), and
closed issues on the same subsystem (`gh -R litedb-org/LiteDB issue list --state closed --search ...`).

Lenses: lifetime/teardown; concurrency and waits; contracts and callers; failure model and
fault points; hot paths and wait primitives; prior art.

Request:
{REQUEST}

Return ONLY this schema (markdown headings, terse bullets; say what you could not cover):

## Invariants
- always: <condition that must hold every time> (where to assert it; which oracle: Deadline, ConnectionClean, Quiescent, ScratchLive, Ownership, Durable, FaultReached/FaultDisposed, or new)
## Reachability markers
- <family>:<name> - the situation a campaign must reach (api:, situation:, maintenance:, refusal:)
## Fuzz dimensions and scenarios
- target (transaction-interleavings, lifetime-chaos, chaos-maintenance, teardown-faults, ...) + dimension value or scenario, with the permitted outcomes of each racing operation
## Manifest claims
- call / change kind / before -> after / the doc sentence that must promise it
## Callers to re-check
- method -> caller -> why its contract may break
## Not covered
- what this lens could not judge and why
