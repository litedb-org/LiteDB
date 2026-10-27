# Mapper clone attribute registrations

Cross-check finding F2: the opt-in attribute support introduced by
[PR #2722](https://github.com/litedb-org/LiteDB/pull/2722) drops all four
registration arrays when a database clones a cold `BsonMapper.Global`.
Ignored fields are persisted and the configured key is replaced by an ObjectId.
Built-in BSON attributes and fluent mappings are controls, not affected features.

The proof pins published NuGet **6.0.0-prerelease.355**. It writes real files,
closes them, then reopens with an independent mapper to inspect raw BSON.
Twenty cases combine type/name/DataAnnotations/native/fluent configuration,
cold/warm entity caches, and Global/explicit mapper selection.

- Exit **0**, `CLONE_DEFECT_VERIFIED`: exactly the three cold Global opt-in
  cases persist the wrong ID and secret; all 17 controls pass.
- Exit **2**, `FIXED_VERIFIED`: all 20 cases persist the exact expected BSON.
- Exit **1**: an unexpected exception, unrelated failure, or partial fix;
  neither variant accepts this as proof.

Run only this focused reproduction:

```bash
dotnet run --project LiteDB.ReproRunner/LiteDB.ReproRunner.Cli -c Release -- \
  run Issue_2722_MapperCloneAttributes --ci --report artifacts_temp/mapper-clone-proof.json
```

The permanent xUnit guard is `AttributeMappingClone_Tests`: it independently
covers all four registration APIs, DataAnnotations, raw persisted fields, ID
lookup, unrelated records, and registration isolation in both directions.
These are graceful close/reopen tests on normal storage, with no crash or
power-loss claim. The mapper fix changes no storage transition or file format.
