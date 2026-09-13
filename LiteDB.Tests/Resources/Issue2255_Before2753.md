# Dictionary compatibility fixture for PR #2753

`Issue2255_Before2753.db` was created using LiteDB commit
`094f2b8564d65ae37951e4d15104c624c53dafdd`, the first parent of merge
`40546f07f5f5c54495249f0e20b555ba2ab747a0` (PR #2753).
The writer ran on .NET 8 with `CurrentCulture = de-AT`, using the ordinary
typed collection API below. No BSON fields or database bytes were patched.

The enum starts at one, so a newly constructed order has an unnamed zero status.
The old library could save and load this dictionary. The merged serializer
can still read it, but throws when saving the same document, even when only
`Name` changes. The compatibility tests copy the fixture before each run.

## Verification

The original 18 compatibility cases were run against the parent and merge commits
on .NET 8, and against unmodified `56272498` on .NET 8 and .NET 10. Each scenario runs under
`de-AT`, `fr-FR`, and `en-US`, using file-backed databases and default mapping.

| Scenario | Before PR | After PR, before the enum fix |
| --- | --- | --- |
| Default zero enum dictionary key | 3 pass | 3 fail on insert |
| Unnamed HTTP status dictionary key | 3 pass | 3 fail on insert |
| Update only Name in the old fixture | 3 pass | 3 fail on update |
| Named enum dictionary keys | 3 pass | 3 pass |
| Decimal dictionary keys | US passes; AT and FR fail | 3 pass |
| Distinct comma/dot string keys | 3 pass | 3 pass |

The nine new failures all come from `EnumConverter.ConvertTo`: it rejects
unnamed values of non-flags enums, although the previous `ToString()` writer
and the unchanged reader both accept their numeric representation.
The five existing `Issue2255_Tests` cases still pass after the merge.

Run the focused tests from the repository root:

```sh
dotnet test LiteDB.Tests/LiteDB.Tests.csproj -c Release -f net8.0 \
  -p:TestingEnabled=true --settings tests.runsettings \
  --filter FullyQualifiedName~Issue2255
```

The enum fix uses `Enum.ToString()` when the selected converter is exactly
the framework's default `EnumConverter`, directly or inside the default
`NullableConverter`. Only exact framework converter types are bypassed, so
custom enum converters and custom nullable wrappers retain their contracts.
This preserves existing names and numeric keys without changing invariant
numeric dictionary formatting. The regression tests assert successful
persistence, including updating the pre-PR fixture without any migration.

Review added six nullable-enum cases across the same three cultures: new writes
and unrelated updates to the original fixture. All six failed against the
initial fix in `b17acf7f` and pass after handling the default nullable wrapper.
Three more tests verify the stored spelling and typed reads for a custom enum
converter, a default nullable wrapper around it, and a custom nullable wrapper.

With the reviewed fix, all 84 focused mapper/Issue2255 tests pass on both
.NET 8 and .NET 10. The full .NET 8 test project passes: 650 passed,
7 skipped, 0 failed.

## Fixture generation

To regenerate, reference the library from that commit in a temporary .NET 8
console project, and run this program with a fresh output filename as `args[0]`:

```csharp
using System.Collections.Generic;
using System.Globalization;
using LiteDB;

CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-AT");
using var db = new LiteDatabase(args[0]);
db.GetCollection<Summary>("summaries").Insert(new Summary
{
    Id = 1,
    Name = "original",
    Counts = new Dictionary<OrderStatus, int>
    {
        [new Order().Status] = 1,
        [OrderStatus.Pending] = 2
    }
});

public enum OrderStatus { Pending = 1, Completed = 2 }
public class Order { public OrderStatus Status { get; set; } }
public class Summary
{
    public int Id { get; set; }
    public string Name { get; set; }
    public Dictionary<OrderStatus, int> Counts { get; set; }
}
```
