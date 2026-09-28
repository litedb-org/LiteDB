## Summary

<!-- What changes and why. Link the issue. -->

## Safety / regression evidence

<!--
Keep this short: link tests, runs and reports instead of pasting logs. Rules and
examples: docs/rules/safety-evidence.md. Name every contract id that the
"Safety contracts" CI step reports for your changed paths.
A documentation-only change may replace the five bullets with one line:
"- Not applicable: <why no previously supported behavior can change>".
-->

- **Contracts and risk:** <!-- What stays supported, what intentionally changes, and why? -->
- **Prior states and interactions:** <!-- Which existing files, callers, modes and neighboring mechanisms are affected? -->
- **Failure outcomes:** <!-- Which transitions change, and what must happen on failure, retry, disposal or reopen? -->
- **Evidence:** <!-- Tests/oracles, proof the risky path ran, failing-before evidence where applicable, tested revision/run. -->
- **Coverage delta and residual concerns:** <!-- Tests removed/weakened/replaced, remaining gaps, the strongest plausible counterexample. -->
