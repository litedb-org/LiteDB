# Custom ID setter regression in v11 candidate 1

Candidate `cef2fd1d1724f030468c301d52526d188572b6f9` passed compressed CI
[35081733552](https://github.com/litedb-org/LiteDB/actions/runs/35081733552),
but an additional independent review found this compatibility regression.
**Do not integrate this candidate without resolving the finding.** The three
hosted reviews are pending; this local finding is additional evidence, not a
replacement or fabricated hosted review result.

## Confirmed before/after

A public `BsonMapper.ResolveMember` callback configures a String-declared ID
member with a custom setter that accepts an ObjectId and stores its hexadecimal
string, plus matching serialization/deserialization delegates. It leaves the
declared `DataType` unchanged.

The same .NET 8 Release scratch harness ran against exact Git exports:

| Source | Observed result |
| --- | --- |
| Integrated base `bbb0253bc06324f0bb14a21a727a37e8c7f2b213` | Insert succeeds, returns ObjectId, copies its value to the String ID, stores one ObjectId-keyed row, and reopens the typed entity with the same String ID. |
| Candidate `cef2fd1d1724f030468c301d52526d188572b6f9` | Insert throws LiteException 214: `Data type System.String is not assignable from data type LiteDB.ObjectId`. The entity ID stays empty and no row is written. |

Candidate `Insert.cs` lines 106-110 reject solely from `_id.DataType` before
the configured setter can accept the value. The old implementation passed the
generated raw ObjectId to that setter. `MemberMapper.Setter`, `Serialize` and
`Deserialize` are independently configurable public delegates; the callback
runs after the defaults are assigned. This violates the existing contract's
custom-mapper compatibility requirement.

This is distinct from ordinary unsupported String auto-ID generation: that
default mapping must still fail before writes with the required diagnostic.
Do not solve this by dropping the four #2590 tests or allowing post-write failure.
Retain the nullable/get-only/throwing-setter, transaction and batch obligations.

## Differential harness identity

Original `Program.cs` SHA-256:
`7cfd3cd943c35a650d49dbf74d5dde617617e15bf58c3a4d1847883a5e1d7197`.
The exported Insert.cs files matched Git blobs:

- Base: `f77e9668855612fc92ac237069c19bfb8f686856`.
- Candidate: `23a585f37bebb9843a2d012df817abf4029a5802`.

The essential public configuration was:

```csharp
mapper.ResolveMember = (entityType, _, member) =>
{
    if (entityType != typeof(Row) || member.MemberName != nameof(Row.Id)) return;

    member.Serialize = (value, _) =>
    {
        var text = (string)value;
        return string.IsNullOrEmpty(text)
            ? new BsonValue(ObjectId.Empty)
            : new BsonValue(new ObjectId(text));
    };
    member.Deserialize = (value, _) => value.AsObjectId;
    member.Setter = (target, value) =>
        ((Row)target).Id = ((ObjectId)value).ToString();
};
```

`Row` has public settable String `Id` and `Name` properties. Insert an instance
with `Id = string.Empty`, dispose the database, then independently reopen both
raw and typed collections. The failure reproduced without modifying repository
files, frozen tests, declared member type or the candidate.
