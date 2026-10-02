## Summary

<!-- What changes and why. Link the issue. -->

## Safety / regression evidence

<!--
Keep this short: link tests, runs and reports instead of pasting logs. Rules and
examples: docs/rules/safety-evidence.md. Name every contract id that the
"Safety contracts" CI step reports for your changed paths. Every answer points at
an artifact (test, run, marker, report); "thread-safe", "crash-safe" or "no risk"
without one is not evidence.
A documentation-only change may replace the five bullets with one line:
"- Not applicable: <why no previously supported behavior can change>".
Bug fix (label `bug` or `bugfix-fix`)? Add a regression proof that fails on a real known-bad
LiteDB and passes here: python .github/scripts/regression_proof.py new ...
-->

- **Contracts and risk:** <!-- What stays supported, what intentionally changes, and why? -->
- **Prior states and interactions:** <!-- Which existing files, callers, modes and neighboring mechanisms are affected? -->
- **Failure outcomes:** <!-- Which transitions change, and what must happen on failure, retry, disposal or reopen? -->
- **Evidence:** <!-- Tests/oracles, proof the risky path ran, failing-before evidence where applicable, tested revision/run. -->
- **Coverage delta and residual concerns:** <!-- Tests removed/weakened/replaced, remaining gaps, the strongest plausible counterexample. -->

## Critical change evidence

<!--
Required only when the change is critical: CI labels it `critical` from the diff
(python .github/scripts/classify_critical.py --base <merge-base>). Otherwise delete
this section. Generate it from the artifacts, do not write it as prose:
  python .github/scripts/render_critical_sections.py --base <merge-base> \
    --reachability <reachability.json> [--proofs proofs.json] [--blast-radius blast.json] \
    [--benchmark <report.md> | --no-hot-path "<reason>"]
A subsection with nothing to list says "None: <reason>" (20+ characters); a reason
never replaces an entry the artifacts require. Procedure: docs/rules/implement-safely.md.
-->

### Intended changes

<!-- Every entry this change adds to .github/safety/intended-changes.json: call [dimension] change: before → after (doc). -->

### Markers declared

<!-- Every marker the diff declares (check_reachability.py --base), with its campaign hit count. -->

### Proofs

<!-- known-bad commit → net → seed / failure id, for each finding fixed and each net-proofs/regression-proofs entry added. -->

### Blast radius

<!-- widened contract → caller → disposition (safe under the new contract / changed / tested by ...). -->

### Benchmark diff

<!-- The contention / shared-slot diff against base with its tolerance, or "No hot path touched: <reason>". -->
