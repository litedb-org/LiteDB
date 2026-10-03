---
name: implement-safely
description: Procedure for critical LiteDB changes (lifetime, ownership, locks, waits, teardown, durability, exception contracts, public API, safety machinery) - blast radius, isolated adversarial lenses, executable spec first, proof before fix, pre-review gates, and the generated critical PR sections. Use when classify_critical.py or the `critical` label says the change is critical, or before changing LiteDB/Engine/Services, LiteDB/Client/Shared, a Dispose/Close/finally path, a wait or lock, or an ILite* interface.
---

# implement-safely

The procedure lives in one place: **read `docs/rules/implement-safely.md` and follow it.**
This file only adds how to run it with Claude Code. Do not restate or override the doc here.

1. Classify first:
   `python .github/scripts/classify_critical.py --base "$(git merge-base HEAD origin/dev)"`.
   Not critical: the ordinary rules in `AGENTS.md` apply; stop here.
2. Phase 1 yourself (blast radius). Keep `safety-plan.md` outside the repository tree.
3. Phase 2: spawn 4-6 lens subagents in ONE message (parallel), each with a fresh context,
   using `prompts/lens.md` with `{LENS}` and `{REQUEST}` filled in. Pass only the feature
   request and the base revision: never your design, plan or reasoning. Merge their returns
   into the executable spec (phase 3) and commit it before production code.
4. Phase 6: spawn the gates with their prompts, each seeing only its column of the
   visibility table in the doc:
   - `prompts/blast-radius.md` (final diff), `prompts/revert-each-fix.md` (use worktree
     isolation; it rewrites history in its own copy only),
   - `prompts/falsifier.md` and `prompts/whole-tree-reviewer.md` (prefer a different model
     family when one is available; do not give them the PR description or your tests).
5. Generate the PR's `## Critical change evidence` with `render_critical_sections.py` and
   check it with `check_pr_section.py --labels '["critical"]'` before opening the PR.

Report MANDATORY obligations as done or blocked; report advisory and pilot nets with what
they found and their stated limits. Never describe reproduction-level or tuned evidence as
generic, or a not-applicable result as a pass.
