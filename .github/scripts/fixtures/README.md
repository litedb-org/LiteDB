# Pinned daily guard replay

`gh_aw_21e402d7_daily_guard.cjs` is the unmodified upstream helper from
`JKamsker/gh-aw` commit `21e402d7a4b5367258a7692cbb84fee60b507598`, path
`actions/setup/js/check_daily_aic_workflow_guardrail.cjs`. It is retained as a
test fixture under the upstream MIT license. Its SHA-256 is
`3f9013365811519fe831ceb46634a9dae2bc77d80a53e62985a5f90de0a0eead`.

The replay executes this exact helper with in-memory GitHub responses. In the
three-review case, the restored cache contains only one review; the helper must
call the artifact reader for both other completed runs and include all three.
This preserves the actual per-workflow cache-miss behavior rather than testing
a separately invented summation algorithm. No network or model call is made.
