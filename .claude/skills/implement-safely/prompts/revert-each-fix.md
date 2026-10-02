# Revert-each-fix gate

You work in an isolated worktree. You see the fix commits {FIXES}, the pinned cases (corpus
entries, explorer vectors, Coyote traces, PBT seeds, regression proofs) that the PR names,
and the CI commands in `docs/rules/validation.md` and `LiteDB.Fuzz/README.md`.

For each fix commit, alone:
1. `git revert --no-commit <fix>` on top of the PR head (resolve only mechanical conflicts).
2. Build (`dotnet build LiteDB.sln -c Release -p:TestingEnabled=true`) and run the case
   pinned for that fix with its recorded seed.
3. Expect the pinned yell (same failure id). Record `fix -> case -> fired (id, seed) |
   NOT FIRED`. Then `git reset --hard` to the head before the next fix.

A fix whose revert does not yell has no proof: report it; do not edit the case to make it
fire. Classify a native-thread non-reproduction (schedule, environment, harness) before
concluding. Return rows for the PR's *Proofs* section: `known-bad -> net -> seed (id)`.
