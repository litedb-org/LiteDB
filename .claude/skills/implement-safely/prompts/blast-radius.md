# Blast-radius step

You see the diff `{BASE}..{HEAD}` and may read and search the whole tree.

1. List every method whose contract widens: it starts throwing or throws a new type
   (added `throw`), stops catching (removed `catch`), blocks or waits where it did not,
   changes what it disposes or releases, or changes ownership/threading requirements.
   Start from `python .github/scripts/classify_critical.py --base {BASE} --head {HEAD}`
   (rules `exception-contract`, `teardown-path`, `obligation:lock-wait`) and confirm by reading.
2. For each, find every caller (grep the name, interface implementations, delegates,
   `using` blocks, finally blocks; Roslyn `SymbolFinder.FindCallersAsync` when available).
3. Return JSON for `render_critical_sections.py --blast-radius`:
   `[{"contract": "Type.Method", "caller": "Type.Caller (path:line)", "disposition": "safe: <why> | changed in this PR | needs test: <which> | BROKEN: <how>"}]`
   Return `[]` only when no contract widens, and say why. Unchanged callers of a callee
   that started throwing are the usual miss: check each one's cleanup still runs.
