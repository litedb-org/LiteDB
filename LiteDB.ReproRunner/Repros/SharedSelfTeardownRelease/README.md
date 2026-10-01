# Shared teardown releases native ownership inside a caller callback

Uses the production sources in [SharedSelfTeardownProof](../SharedSelfTeardownProof/README.md)
in `--release-only` mode. The known-bad package is `6.0.0-prerelease.319`;
source must refuse same-instance disposal during executing core teardown before
mutating ownership. Plain and encrypted cases plus positive controls are required.
This safety defect predates the transaction-handle PR.
