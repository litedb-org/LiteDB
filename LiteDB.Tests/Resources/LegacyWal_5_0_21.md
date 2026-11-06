# Genuine legacy WAL recovery control

`LegacyWal_5_0_21.zip` contains `plain.db`, `plain-log.db`, `encrypted.db` and
`encrypted-log.db` produced by the published LiteDB **5.0.21** NuGet package.
The encrypted password is `wal-secret`. Each pair contains a checkpointed
`rows` document `{_id: 1, value: 7}` and a committed WAL-only document
`{_id: 2, value: 8}`. No data or WAL bytes were patched after generation.

Generator: `tools/LegacyWalFixture`, a separate released-package project.
Generate once, capturing both streams before disposal checkpoints them:

```bash
dotnet run --project tools/LegacyWalFixture -c Release -- \
  LiteDB.Tests/Resources/LegacyWal_5_0_21.zip
```

Recorded producer: Linux x64 (kernel 6.8.0-146-generic), SDK 10.0.400,
net8.0, Release, binary collation, 2026-10-27. NuGet package SHA-256:
`938a4f5f9d6a28383de8ccfcbdb8897e78432399215232cf38dbf147e03ec167`;
netstandard2.0 DLL SHA-256:
`ae31ac6a93549217b9e8b81497d1ee831658ade6df9e1ee20f2838f22de5e218`.
Archive SHA-256: `0b89374671dc56cd28e15ca89a04390b622ab3bb689a174852f64b22bd68d603`.
Creation time, encryption salt and ZIP timestamps vary on regeneration.

`ConvertedWalFormat_Tests.Genuine_5_0_21_wal_recovers_both_rows_and_reopens`
checks both complete documents on two opens and independently checks that the
released data without its WAL contains only row 1. This defeats a guard that
rejects all legacy WAL or silently drops the WAL-only commit. The same class's
later-page format test appends a synthetic committed marked page in memory;
that deliberate corrupt-input variant does not alter this archive.
