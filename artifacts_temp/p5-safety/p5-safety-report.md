# P5 safety qualification

Candidate: `e507faad40e968aa2fcf63ae6dd01fd7def2e07b`. Driver: `v6/wip-p5-campaigns@b811372fb09ea469ea884d06df22002225df4d71`.

Run: https://github.com/litedb-org/LiteDB/actions/runs/42531054661. All product and harness binaries build from the exact candidate checkout; the scratch driver is external. No product changes.

| Target | Platform/job | Kind | Seeds | Steps | Wall seconds (sum) | Failures | Reachability |
| --- | --- | --- | ---: | ---: | ---: | ---: | --- |
| api-boundary | p5-linux-0 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 3480 | 2024.342 | 10 | 30 distinct recorded markers; see audit |
| api-boundary | p5-linux-0 | replay | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 3480 | 2082.933 | 10 | 30 distinct recorded markers; see audit |
| api-boundary | p5-windows-0 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 3480 | 2473.696 | 10 | 30 distinct recorded markers; see audit |
| api-boundary | p5-windows-0 | replay | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 3480 | 2511.343 | 10 | 30 distinct recorded markers; see audit |
| boundary | p5-linux-1 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 10 | 11.927 | 0 | 30 distinct recorded markers; see audit |
| boundary | p5-windows-1 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 10 | 13.759 | 0 | 30 distinct recorded markers; see audit |
| bson | p5-windows-2 | campaign | 9,42947,529470,529471,529472,529473,529474,529475,529476,529477,529478,529479,900000 | 4670 | 8.871 | 0 | 0 distinct recorded markers; see audit |
| chaos | p5-linux-3 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4000 | 37.704 | 0 | 62 distinct recorded markers; see audit |
| chaos | p5-windows-3 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4000 | 21.105 | 0 | 62 distinct recorded markers; see audit |
| checksum-crash | p5-linux-4 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479,3163459,4058733 | 7120 | 545.649 | 0 | 31 distinct recorded markers; see audit |
| checksum-crash | p5-windows-4 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479,3163459,4058733 | 7120 | 421.636 | 0 | 31 distinct recorded markers; see audit |
| checksum-migration | p5-linux-5 | campaign | 2954,529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4280 | 358.232 | 0 | 26 distinct recorded markers; see audit |
| checksum-migration | p5-windows-5 | campaign | 2954,529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4280 | 321.594 | 0 | 26 distinct recorded markers; see audit |
| checksum-page | p5-linux-6 | campaign | 2954,529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4280 | 112.619 | 0 | 26 distinct recorded markers; see audit |
| checksum-page | p5-windows-6 | campaign | 2954,529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4280 | 87.546 | 0 | 26 distinct recorded markers; see audit |
| checksum-wal | p5-linux-7 | campaign | 2954,529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4280 | 200.609 | 0 | 25 distinct recorded markers; see audit |
| checksum-wal | p5-windows-7 | campaign | 2954,529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4280 | 95.683 | 0 | 25 distinct recorded markers; see audit |
| compact-codec | p5-linux-0 | campaign | 2999,529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4640 | 3.828 | 0 | 1 distinct recorded markers; see audit |
| compact-codec | p5-windows-0 | campaign | 2999,529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4640 | 4.926 | 0 | 1 distinct recorded markers; see audit |
| compact-crash | p5-linux-1 | campaign | 2937,529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4640 | 121.616 | 0 | 37 distinct recorded markers; see audit |
| compact-crash | p5-windows-1 | campaign | 2937,529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4640 | 90.318 | 0 | 37 distinct recorded markers; see audit |
| compact-power-loss | p5-windows-2 | campaign | 2937,529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4640 | 63.565 | 0 | 41 distinct recorded markers; see audit |
| compact-storage | p5-linux-3 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4000 | 42.497 | 0 | 46 distinct recorded markers; see audit |
| compact-storage | p5-windows-3 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4000 | 24.601 | 0 | 46 distinct recorded markers; see audit |
| compatibility | p5-linux-4 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4000 | 266.499 | 0 | 43 distinct recorded markers; see audit |
| compatibility | p5-windows-4 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4000 | 328.703 | 0 | 42 distinct recorded markers; see audit |
| concurrent | p5-linux-5 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479,1681725734,1682725737 | 6450 | 106.431 | 0 | 40 distinct recorded markers; see audit |
| concurrent | p5-windows-5 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479,1681725734,1682725737 | 6450 | 515.440 | 0 | 40 distinct recorded markers; see audit |
| conflict | p5-linux-6 | campaign | 2947,529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4030 | 169.747 | 0 | 41 distinct recorded markers; see audit |
| conflict | p5-windows-6 | campaign | 2947,529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4030 | 173.698 | 0 | 41 distinct recorded markers; see audit |
| cursor-handoff | p5-linux-7 | campaign | 2991,529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4200 | 149.155 | 0 | 62 distinct recorded markers; see audit |
| cursor-handoff | p5-windows-7 | campaign | 2991,529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4200 | 188.233 | 0 | 62 distinct recorded markers; see audit |
| index | p5-linux-0 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479,2905001 | 6000 | 28.145 | 0 | 29 distinct recorded markers; see audit |
| index | p5-windows-0 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479,2905001 | 6000 | 38.699 | 0 | 29 distinct recorded markers; see audit |
| integrity | p5-linux-1 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 10 | 5.881 | 0 | 39 distinct recorded markers; see audit |
| integrity | p5-windows-1 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 10 | 6.257 | 0 | 38 distinct recorded markers; see audit |
| linq-cache | p5-windows-2 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479,2944001,2944002 | 8000 | 56.942 | 0 | 0 distinct recorded markers; see audit |
| malformed-file | p5-linux-3 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4000 | 26.172 | 0 | 31 distinct recorded markers; see audit |
| malformed-file | p5-windows-3 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4000 | 22.769 | 0 | 31 distinct recorded markers; see audit |
| mapper | p5-linux-4 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4000 | 4.709 | 0 | 23 distinct recorded markers; see audit |
| mapper | p5-windows-4 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4000 | 4.476 | 0 | 23 distinct recorded markers; see audit |
| mvcc-checkpoint | p5-linux-5 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479,3000003 | 4640 | 457.712 | 0 | 49 distinct recorded markers; see audit |
| mvcc-checkpoint | p5-windows-5 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479,3000003 | 4640 | 406.615 | 0 | 49 distinct recorded markers; see audit |
| mvcc-retirement | p5-linux-6 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479,3000002 | 4300 | 273.256 | 0 | 54 distinct recorded markers; see audit |
| mvcc-retirement | p5-windows-6 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479,3000002 | 4300 | 284.122 | 0 | 54 distinct recorded markers; see audit |
| oracle-selftest | p5-linux-7 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4000 | 9.252 | 0 | 27 distinct recorded markers; see audit |
| oracle-selftest | p5-windows-7 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4000 | 6.426 | 0 | 27 distinct recorded markers; see audit |
| page | p5-linux-0 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4000 | 1.042 | 0 | 0 distinct recorded markers; see audit |
| page | p5-windows-0 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4000 | 1.252 | 0 | 0 distinct recorded markers; see audit |
| parser | p5-linux-1 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479,2946001 | 5000 | 88.934 | 0 | 23 distinct recorded markers; see audit |
| parser | p5-windows-1 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479,2946001 | 5000 | 94.138 | 0 | 23 distinct recorded markers; see audit |
| power-loss | p5-windows-2 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4000 | 85.897 | 0 | 32 distinct recorded markers; see audit |
| pressure | p5-linux-3 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4000 | 104.575 | 0 | 50 distinct recorded markers; see audit |
| pressure | p5-windows-3 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4000 | 72.916 | 0 | 50 distinct recorded markers; see audit |
| query | p5-linux-4 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4000 | 63.709 | 0 | 23 distinct recorded markers; see audit |
| query | p5-windows-4 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4000 | 65.977 | 0 | 23 distinct recorded markers; see audit |
| read-only | p5-linux-5 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4000 | 11.985 | 0 | 30 distinct recorded markers; see audit |
| read-only | p5-windows-5 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4000 | 18.677 | 0 | 29 distinct recorded markers; see audit |
| rebuild | p5-linux-6 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4000 | 187.283 | 0 | 40 distinct recorded markers; see audit |
| rebuild | p5-windows-6 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4000 | 148.169 | 0 | 40 distinct recorded markers; see audit |
| rebuild-transition | p5-linux-7 | campaign | 9947,529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4060 | 161.533 | 0 | 42 distinct recorded markers; see audit |
| rebuild-transition | p5-windows-7 | campaign | 9947,529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4060 | 144.085 | 0 | 42 distinct recorded markers; see audit |
| recovery | p5-linux-0 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4000 | 17.985 | 0 | 25 distinct recorded markers; see audit |
| recovery | p5-windows-0 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4000 | 21.304 | 0 | 25 distinct recorded markers; see audit |
| shared | p5-linux-shared | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4000 | 6628.718 | 0 | 33 distinct recorded markers; see audit |
| shared | p5-macos-shared-handle | campaign | 529470,529471 | 682 | 14850.238 | 1 | 33 distinct recorded markers; see audit |
| shared | p5-windows-shared | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4000 | 13606.490 | 0 | 34 distinct recorded markers; see audit |
| shared-legacy | p5-linux-shared | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4000 | 3827.415 | 0 | 33 distinct recorded markers; see audit |
| shared-legacy | p5-windows-shared | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4000 | 5124.910 | 0 | 34 distinct recorded markers; see audit |
| shared-wake | p5-linux-1 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4000 | 22.298 | 0 | 1 distinct recorded markers; see audit |
| shared-wake | p5-windows-1 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 0 | 0.502 | 0 | 0 distinct recorded markers; see audit |
| snapshot | p5-windows-2 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 508 | 1891.452 | 10 | 48 distinct recorded markers; see audit |
| snapshot | p5-windows-2 | replay | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 273 | 970.227 | 9 | 48 distinct recorded markers; see audit |
| sort | p5-linux-3 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4000 | 63.873 | 0 | 24 distinct recorded markers; see audit |
| sort | p5-windows-3 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4000 | 46.084 | 0 | 24 distinct recorded markers; see audit |
| sql-dml | p5-linux-4 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4000 | 6.709 | 0 | 24 distinct recorded markers; see audit |
| sql-dml | p5-windows-4 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4000 | 6.667 | 0 | 24 distinct recorded markers; see audit |
| storage | p5-linux-5 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479,700001 | 4120 | 92.435 | 0 | 54 distinct recorded markers; see audit |
| storage | p5-windows-5 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479,700001 | 4120 | 109.893 | 0 | 54 distinct recorded markers; see audit |
| storage-failure | p5-linux-6 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4000 | 68.710 | 0 | 24 distinct recorded markers; see audit |
| storage-failure | p5-windows-6 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4000 | 64.495 | 0 | 24 distinct recorded markers; see audit |
| threaded-snapshot | p5-linux-7 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4000 | 9833.648 | 0 | 51 distinct recorded markers; see audit |
| threaded-snapshot | p5-windows-7 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4000 | 4825.579 | 0 | 51 distinct recorded markers; see audit |
| transaction | p5-linux-0 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479,700001 | 4260 | 10.805 | 0 | 29 distinct recorded markers; see audit |
| transaction | p5-windows-0 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479,700001 | 4260 | 14.872 | 0 | 29 distinct recorded markers; see audit |
| transaction-gate | p5-linux-1 | campaign | 2991,529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4200 | 15.619 | 0 | 1 distinct recorded markers; see audit |
| transaction-gate | p5-windows-1 | campaign | 2991,529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4200 | 25.281 | 0 | 1 distinct recorded markers; see audit |
| transaction-handle | p5-windows-2 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4000 | 119.438 | 0 | 49 distinct recorded markers; see audit |
| value | p5-linux-3 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4000 | 3.632 | 0 | 23 distinct recorded markers; see audit |
| value | p5-windows-3 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4000 | 2.185 | 0 | 23 distinct recorded markers; see audit |
| vector | p5-linux-4 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 3305 | 46.995 | 3 | 38 distinct recorded markers; see audit |
| vector | p5-linux-4 | replay | 529476,529477,529478 | 504 | 28.530 | 3 | 37 distinct recorded markers; see audit |
| vector | p5-windows-4 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 3305 | 47.583 | 3 | 38 distinct recorded markers; see audit |
| vector | p5-windows-4 | replay | 529476,529477,529478 | 504 | 30.211 | 3 | 37 distinct recorded markers; see audit |
| wal | p5-linux-5 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4000 | 32.574 | 0 | 33 distinct recorded markers; see audit |
| wal | p5-windows-5 | campaign | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 4000 | 32.887 | 0 | 33 distinct recorded markers; see audit |

Binary SHA256: [all-binary-sha256.json](p5-safety-campaigns-evidence/all-binary-sha256.json); exact binaries, recorded inputs, run metrics, traces and replay files retained in hosted artifacts and evidence/artifacts.

Reported campaign steps (including attempted failing steps and corpus, excluding replays): 365110.
Fuzz failures including replays: 72; see all-failures.json. Fault-test counts: trx-counts.json.

Residual risks: finite generated schedules and fault scenarios do not cover all execution interleavings or hardware failures. Process death retains OS cache; modeled power loss covers only durability semantics encoded by the test streams. No physical device, VM or OS reset was performed. Network filesystems, cross-host locking, server/client cache coherence and distributed durability are unqualified. Unsynced directory-entry loss and reader lease publication/renewal/expiry hook gaps remain inherited limits. shared-wake is Linux-specific and returns without steps on Windows/macOS; portable Shared and handle targets still run there.

State: campaign terminal; candidate qualification blocked by reproduced #2952 and explicit unqualified reachability gaps.


## Terminal accounting

[Requested matrix versus observed targets/seeds/source SHA](p5-safety-campaigns-evidence/inventory-check.json).
All 22 jobs terminal: {'failure': 5, 'success': 15, 'cancelled': 2}. Expected artifact legs: 22; downloaded: 21; missing: ['linux-2'].
Distinct targets: 48. Campaign steps including unchanged regression corpus, excluding failure replays and local PR3142 comparison: 365110. Source provenance counts: {'e507faad40e968aa2fcf63ae6dd01fd7def2e07b': 1342}.
Fault-test result outcomes: {'Passed': 5318, 'NotExecuted': 8}. Tests returning early for unsupported platforms can report Passed without exercising the platform path; marker audit remains authoritative for recorded reachability.

| Job | Conclusion | Job log |
| --- | --- | --- |
| linux-0 | failure | [hosted log](https://github.com/litedb-org/LiteDB/actions/runs/42531054661/job/128885108507) |
| windows-5 | success | [hosted log](https://github.com/litedb-org/LiteDB/actions/runs/42531054661/job/128885108595) |
| linux-7 | success | [hosted log](https://github.com/litedb-org/LiteDB/actions/runs/42531054661/job/128885108616) |
| linux-1 | success | [hosted log](https://github.com/litedb-org/LiteDB/actions/runs/42531054661/job/128885108636) |
| windows-6 | success | [hosted log](https://github.com/litedb-org/LiteDB/actions/runs/42531054661/job/128885108657) |
| linux-4 | failure | [hosted log](https://github.com/litedb-org/LiteDB/actions/runs/42531054661/job/128885108674) |
| windows-7 | success | [hosted log](https://github.com/litedb-org/LiteDB/actions/runs/42531054661/job/128885108677) |
| windows-2 | failure | [hosted log](https://github.com/litedb-org/LiteDB/actions/runs/42531054661/job/128885108680) |
| macos-faults | success | [hosted log](https://github.com/litedb-org/LiteDB/actions/runs/42531054661/job/128885108705) |
| windows-0 | failure | [hosted log](https://github.com/litedb-org/LiteDB/actions/runs/42531054661/job/128885108706) |
| windows-4 | failure | [hosted log](https://github.com/litedb-org/LiteDB/actions/runs/42531054661/job/128885108712) |
| windows-3 | success | [hosted log](https://github.com/litedb-org/LiteDB/actions/runs/42531054661/job/128885108719) |
| windows-1 | success | [hosted log](https://github.com/litedb-org/LiteDB/actions/runs/42531054661/job/128885108723) |
| linux-2 | cancelled | [hosted log](https://github.com/litedb-org/LiteDB/actions/runs/42531054661/job/128885108734) |
| windows-faults | success | [hosted log](https://github.com/litedb-org/LiteDB/actions/runs/42531054661/job/128885108741) |
| linux-6 | success | [hosted log](https://github.com/litedb-org/LiteDB/actions/runs/42531054661/job/128885108767) |
| linux-shared | success | [hosted log](https://github.com/litedb-org/LiteDB/actions/runs/42531054661/job/128885108772) |
| macos-shared-handle | cancelled | [hosted log](https://github.com/litedb-org/LiteDB/actions/runs/42531054661/job/128885108784) |
| linux-5 | success | [hosted log](https://github.com/litedb-org/LiteDB/actions/runs/42531054661/job/128885108788) |
| linux-faults | success | [hosted log](https://github.com/litedb-org/LiteDB/actions/runs/42531054661/job/128885108809) |
| linux-3 | success | [hosted log](https://github.com/litedb-org/LiteDB/actions/runs/42531054661/job/128885108826) |
| windows-shared | success | [hosted log](https://github.com/litedb-org/LiteDB/actions/runs/42531054661/job/128885108883) |

## Requested invocations without retained run.json

| Target | Job/platform | Seeds without retained run.json | Recorded runs | Recorded steps |
| --- | --- | --- | ---: | ---: |
| bson | linux-2 | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 0 | 0 |
| compact-power-loss | linux-2 | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 0 | 0 |
| linq-cache | linux-2 | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 0 | 0 |
| power-loss | linux-2 | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 0 | 0 |
| snapshot | linux-2 | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 0 | 0 |
| transaction-handle | linux-2 | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 0 | 0 |
| shared | macos-shared-handle | 529472,529473,529474,529475,529476,529477,529478,529479 | 2 | 682 |
| shared-legacy | macos-shared-handle | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 0 | 0 |
| shared-wake | macos-shared-handle | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 0 | 0 |
| transaction-handle | macos-shared-handle | 529470,529471,529472,529473,529474,529475,529476,529477,529478,529479 | 0 | 0 |

Interrupted invocations may lack run.json and are excluded from step totals. No count is imputed for them. Successful records from cancelled legs remain bounded evidence, not a completed leg.

## Classified findings

Known product defect [#2952](https://github.com/litedb-org/LiteDB/issues/2952), duplicate #2980: vector seeds529476/529477/529478 fail on Linux and Windows; six original failures plus six independent same-identity hosted replays. Failure identity VECTOR_DATABASEVECTORINTEGRITYVERIFIER_L43. [Detailed classification, commands and evidence](p5-safety-campaigns-evidence/vector-finding.md); raw candidate failure/replay artifacts are retained. No newly introduced Phase4 origin is claimed.

**Does PR3142 fix #2952? Yes for every exercised case.** At `c2776273fe490d8e06d58a68e2d59fb85685ba51`, all three seeds pass400 steps; minimal prefix100 (plus80) passes. The entire original input is a byte-identical prefix of each passing regenerated run. [Commands and counts](p5-safety-campaigns-evidence/pr3142-replays/commands.json), [input comparison](p5-safety-campaigns-evidence/pr3142-input-prefix-comparison.json), [binary SHA256](p5-safety-campaigns-evidence/pr3142-replays/sha256.json). Raw original/bounded replay-envelope input exhaustion is a harness limitation; it occurs beyond the old failure point or during post-loop validation and is retained, not represented as a pass. This is local Linux comparison of3142 head, not hosted or integrated candidate-plus3142 qualification.

[Additional classified findings and replay outcomes](p5-safety-campaigns-evidence/supplemental-findings.md): API-boundary capacity exhaustion on Linux/Windows, Windows snapshot IPC sharing/input-exhaustion, the unresolved macOS Shared checkpoint cut plus job-budget cancellation, and linux-2 cancellation with failed artifact upload. Exact deterministic replay is not established for every snapshot seed or the macOS Shared missed checkpoint cut; these qualification gaps remain blockers.

## Reachability and gaps

Registered markers: 173; reached: 115; unreached: 58. Registered hook/injector entries: 100; recorded reached: 83; unreached: 17.

[Every hook/injector, recorded hits, registered test execution, and every unreached marker](p5-safety-campaigns-evidence/reachability-audit.md). [Machine-readable full marker audit](p5-safety-campaigns-evidence/reachability.json), [fault audit](p5-safety-campaigns-evidence/fault-audit.json). xUnit does not emit marker telemetry; passing registered tests are listed separately and do not silently promote zero-hit points to reached.

## Binary SHA256

Independently measured all available core binaries in measured-core-binaries.json, including partial/cancelled legs without end manifests. Verified 185 published hash entries against downloaded file bytes; mismatches/missing: 0. [Complete SHA256 inventory](p5-safety-campaigns-evidence/all-binary-sha256.json), [byte verification](p5-safety-campaigns-evidence/hash-validation.json).

| Leg | Binary | SHA256 |
| --- | --- | --- |
| p5-linux-shared | LiteDB.Fuzz/bin/Release/net8.0/LiteDB.dll | `0b5c202a2c50d16ed65bf65a90ea5c26d4deb17482c1bf5f7468aa399d9b90b9` |
| p5-linux-shared | LiteDB.Fuzz/bin/Release/net8.0/LiteDB.Fuzz.dll | `1673d7adaa04b7b2cb64b593764e0ab624b5d3b1d99ad1a581c1bb502cafe7e2` |
| p5-linux-faults | LiteDB.Tests/bin/Release/net8.0/LiteDB.dll | `0b5c202a2c50d16ed65bf65a90ea5c26d4deb17482c1bf5f7468aa399d9b90b9` |
| p5-linux-faults | LiteDB.Tests/bin/Release/net8.0/handle-probe/LiteDB.dll | `0b5c202a2c50d16ed65bf65a90ea5c26d4deb17482c1bf5f7468aa399d9b90b9` |
| p5-linux-faults | LiteDB.Tests/bin/Release/net8.0/mvcc-probe/LiteDB.dll | `0b5c202a2c50d16ed65bf65a90ea5c26d4deb17482c1bf5f7468aa399d9b90b9` |
| p5-linux-faults | LiteDB.Fuzz/bin/Release/net8.0/LiteDB.dll | `0b5c202a2c50d16ed65bf65a90ea5c26d4deb17482c1bf5f7468aa399d9b90b9` |
| p5-linux-faults | LiteDB.Fuzz/bin/Release/net8.0/LiteDB.Fuzz.dll | `1673d7adaa04b7b2cb64b593764e0ab624b5d3b1d99ad1a581c1bb502cafe7e2` |
| p5-linux-faults | LiteDB.Tests/bin/Release/net8.0/LiteDB.Tests.dll | `829b3ba64f6a77e95c7b9db7eb9fa567cc97f2d0cd4c7f897f8eefba2d92eb53` |
| p5-windows-1 | LiteDB.Fuzz/bin/Release/net8.0/LiteDB.dll | `a4fe0eccf7e03b669bb7bd617d7364c23be741539252ff9e361151453f987677` |
| p5-windows-1 | LiteDB.Fuzz/bin/Release/net8.0/LiteDB.Fuzz.dll | `faa737143c9924fe252030333fc9f80da138351bea6ab0de2d059fafe9cfb125` |
| p5-macos-faults | LiteDB.Tests/bin/Release/net8.0/LiteDB.dll | `75ff946114633c706520c272447c0beccb8043b25fcebb53dedf5b93a85f0e24` |
| p5-macos-faults | LiteDB.Tests/bin/Release/net8.0/handle-probe/LiteDB.dll | `75ff946114633c706520c272447c0beccb8043b25fcebb53dedf5b93a85f0e24` |
| p5-macos-faults | LiteDB.Tests/bin/Release/net8.0/mvcc-probe/LiteDB.dll | `75ff946114633c706520c272447c0beccb8043b25fcebb53dedf5b93a85f0e24` |
| p5-macos-faults | LiteDB.Fuzz/bin/Release/net8.0/LiteDB.dll | `75ff946114633c706520c272447c0beccb8043b25fcebb53dedf5b93a85f0e24` |
| p5-macos-faults | LiteDB.Fuzz/bin/Release/net8.0/LiteDB.Fuzz.dll | `648bcaecf4c754dea2d542a0ef18a88648f2457bbf5b359cc4f210b5e21cf745` |
| p5-macos-faults | LiteDB.Tests/bin/Release/net8.0/LiteDB.Tests.dll | `8ba2c51bb9f55a7ce0f8e292a557965b041552e681bf2b8d37a480d94db53cfe` |
| p5-windows-4 | LiteDB.Fuzz/bin/Release/net8.0/LiteDB.dll | `a4fe0eccf7e03b669bb7bd617d7364c23be741539252ff9e361151453f987677` |
| p5-windows-4 | LiteDB.Fuzz/bin/Release/net8.0/LiteDB.Fuzz.dll | `faa737143c9924fe252030333fc9f80da138351bea6ab0de2d059fafe9cfb125` |
| p5-linux-3 | LiteDB.Fuzz/bin/Release/net8.0/LiteDB.dll | `0b5c202a2c50d16ed65bf65a90ea5c26d4deb17482c1bf5f7468aa399d9b90b9` |
| p5-linux-3 | LiteDB.Fuzz/bin/Release/net8.0/LiteDB.Fuzz.dll | `1673d7adaa04b7b2cb64b593764e0ab624b5d3b1d99ad1a581c1bb502cafe7e2` |
| p5-windows-7 | LiteDB.Fuzz/bin/Release/net8.0/LiteDB.dll | `a4fe0eccf7e03b669bb7bd617d7364c23be741539252ff9e361151453f987677` |
| p5-windows-7 | LiteDB.Fuzz/bin/Release/net8.0/LiteDB.Fuzz.dll | `faa737143c9924fe252030333fc9f80da138351bea6ab0de2d059fafe9cfb125` |
| p5-windows-6 | LiteDB.Fuzz/bin/Release/net8.0/LiteDB.dll | `a4fe0eccf7e03b669bb7bd617d7364c23be741539252ff9e361151453f987677` |
| p5-windows-6 | LiteDB.Fuzz/bin/Release/net8.0/LiteDB.Fuzz.dll | `faa737143c9924fe252030333fc9f80da138351bea6ab0de2d059fafe9cfb125` |
| p5-macos-shared-handle | LiteDB.Fuzz/bin/Release/net8.0/LiteDB.dll | `75ff946114633c706520c272447c0beccb8043b25fcebb53dedf5b93a85f0e24` |
| p5-macos-shared-handle | LiteDB.Fuzz/bin/Release/net8.0/LiteDB.Fuzz.dll | `648bcaecf4c754dea2d542a0ef18a88648f2457bbf5b359cc4f210b5e21cf745` |
| p5-linux-7 | LiteDB.Fuzz/bin/Release/net8.0/LiteDB.dll | `0b5c202a2c50d16ed65bf65a90ea5c26d4deb17482c1bf5f7468aa399d9b90b9` |
| p5-linux-7 | LiteDB.Fuzz/bin/Release/net8.0/LiteDB.Fuzz.dll | `1673d7adaa04b7b2cb64b593764e0ab624b5d3b1d99ad1a581c1bb502cafe7e2` |
| p5-windows-3 | LiteDB.Fuzz/bin/Release/net8.0/LiteDB.dll | `a4fe0eccf7e03b669bb7bd617d7364c23be741539252ff9e361151453f987677` |
| p5-windows-3 | LiteDB.Fuzz/bin/Release/net8.0/LiteDB.Fuzz.dll | `faa737143c9924fe252030333fc9f80da138351bea6ab0de2d059fafe9cfb125` |
| p5-linux-4 | LiteDB.Fuzz/bin/Release/net8.0/LiteDB.dll | `0b5c202a2c50d16ed65bf65a90ea5c26d4deb17482c1bf5f7468aa399d9b90b9` |
| p5-linux-4 | LiteDB.Fuzz/bin/Release/net8.0/LiteDB.Fuzz.dll | `1673d7adaa04b7b2cb64b593764e0ab624b5d3b1d99ad1a581c1bb502cafe7e2` |
| p5-windows-shared | LiteDB.Fuzz/bin/Release/net8.0/LiteDB.dll | `a4fe0eccf7e03b669bb7bd617d7364c23be741539252ff9e361151453f987677` |
| p5-windows-shared | LiteDB.Fuzz/bin/Release/net8.0/LiteDB.Fuzz.dll | `faa737143c9924fe252030333fc9f80da138351bea6ab0de2d059fafe9cfb125` |
| p5-windows-0 | LiteDB.Fuzz/bin/Release/net8.0/LiteDB.dll | `a4fe0eccf7e03b669bb7bd617d7364c23be741539252ff9e361151453f987677` |
| p5-windows-0 | LiteDB.Fuzz/bin/Release/net8.0/LiteDB.Fuzz.dll | `faa737143c9924fe252030333fc9f80da138351bea6ab0de2d059fafe9cfb125` |
| p5-windows-faults | LiteDB.Tests/bin/Release/net8.0/LiteDB.dll | `a4fe0eccf7e03b669bb7bd617d7364c23be741539252ff9e361151453f987677` |
| p5-windows-faults | LiteDB.Tests/bin/Release/net8.0/handle-probe/LiteDB.dll | `a4fe0eccf7e03b669bb7bd617d7364c23be741539252ff9e361151453f987677` |
| p5-windows-faults | LiteDB.Tests/bin/Release/net8.0/mvcc-probe/LiteDB.dll | `a4fe0eccf7e03b669bb7bd617d7364c23be741539252ff9e361151453f987677` |
| p5-windows-faults | LiteDB.Fuzz/bin/Release/net8.0/LiteDB.dll | `a4fe0eccf7e03b669bb7bd617d7364c23be741539252ff9e361151453f987677` |
| p5-windows-faults | LiteDB.Fuzz/bin/Release/net8.0/LiteDB.Fuzz.dll | `faa737143c9924fe252030333fc9f80da138351bea6ab0de2d059fafe9cfb125` |
| p5-windows-faults | LiteDB.Tests/bin/Release/net8.0/LiteDB.Tests.dll | `88e0240b57497312c69200fff226dbdd405c40fc4d2314a6a34d9425ce80d0cb` |
| p5-windows-5 | LiteDB.Fuzz/bin/Release/net8.0/LiteDB.dll | `a4fe0eccf7e03b669bb7bd617d7364c23be741539252ff9e361151453f987677` |
| p5-windows-5 | LiteDB.Fuzz/bin/Release/net8.0/LiteDB.Fuzz.dll | `faa737143c9924fe252030333fc9f80da138351bea6ab0de2d059fafe9cfb125` |
| p5-windows-2 | LiteDB.Fuzz/bin/Release/net8.0/LiteDB.dll | `a4fe0eccf7e03b669bb7bd617d7364c23be741539252ff9e361151453f987677` |
| p5-windows-2 | LiteDB.Fuzz/bin/Release/net8.0/LiteDB.Fuzz.dll | `faa737143c9924fe252030333fc9f80da138351bea6ab0de2d059fafe9cfb125` |
| p5-linux-0 | LiteDB.Fuzz/bin/Release/net8.0/LiteDB.dll | `0b5c202a2c50d16ed65bf65a90ea5c26d4deb17482c1bf5f7468aa399d9b90b9` |
| p5-linux-0 | LiteDB.Fuzz/bin/Release/net8.0/LiteDB.Fuzz.dll | `1673d7adaa04b7b2cb64b593764e0ab624b5d3b1d99ad1a581c1bb502cafe7e2` |
| p5-linux-6 | LiteDB.Fuzz/bin/Release/net8.0/LiteDB.dll | `0b5c202a2c50d16ed65bf65a90ea5c26d4deb17482c1bf5f7468aa399d9b90b9` |
| p5-linux-6 | LiteDB.Fuzz/bin/Release/net8.0/LiteDB.Fuzz.dll | `1673d7adaa04b7b2cb64b593764e0ab624b5d3b1d99ad1a581c1bb502cafe7e2` |
| p5-linux-1 | LiteDB.Fuzz/bin/Release/net8.0/LiteDB.dll | `0b5c202a2c50d16ed65bf65a90ea5c26d4deb17482c1bf5f7468aa399d9b90b9` |
| p5-linux-1 | LiteDB.Fuzz/bin/Release/net8.0/LiteDB.Fuzz.dll | `1673d7adaa04b7b2cb64b593764e0ab624b5d3b1d99ad1a581c1bb502cafe7e2` |
| p5-linux-5 | LiteDB.Fuzz/bin/Release/net8.0/LiteDB.dll | `0b5c202a2c50d16ed65bf65a90ea5c26d4deb17482c1bf5f7468aa399d9b90b9` |
| p5-linux-5 | LiteDB.Fuzz/bin/Release/net8.0/LiteDB.Fuzz.dll | `1673d7adaa04b7b2cb64b593764e0ab624b5d3b1d99ad1a581c1bb502cafe7e2` |

## Lost linux-2 evidence and terminal blockers

The final job log records48 exit0 invocations, but its artifact upload failed with ENOENT on an active snapshot child control file. No linux-2 archive exists. Its binaries cannot be hashed or counted as retained coverage; the365110 total is a **lower bound from retained run.json**, excluding all lost linux-2 metrics and interrupted work. [Terminal log](p5-safety-campaigns-evidence/linux-2-job.log), [parsed commands](p5-safety-campaigns-evidence/linux-2-log-commands.json), [full classification and missing replay proof](p5-safety-campaigns-evidence/supplemental-findings.md). No campaign was redispatched. Candidate qualification is blocked by2952, the unresolved macOS cut, non-deterministic snapshot replay limits, missing raw evidence, and explicit reachability gaps.

## Residual qualification risks

The pinned candidate still contains #2952. A passing PR3142 replay does not alter that candidate or establish that every historical scenario is fixed. Unreached markers and inherited unhooked transitions are coverage gaps, not passes. Local PR comparison is Linux net8 only. Hosted campaign binaries use net8; this campaign does not establish net10/Framework/x86 equivalence or physical-device power-loss behavior. Platform skips/early returns are retained. Fault-test repetition increases schedule diversity without proving every interleaving. Rebuild/upgrade recoverability is bounded by the fault models and scenarios actually executed. See the inherited directory-entry, reader-lease and cross-host limits above.
