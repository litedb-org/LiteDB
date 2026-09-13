# UInt64 BSON compatibility

LiteDB stores `UInt64` values in the exact BSON numeric representation that does
not collide with signed keys:

| CLR value | Current BSON representation |
| --- | --- |
| `0` through `Int64.MaxValue` | `Int64` |
| `Int64.MaxValue + 1` through `UInt64.MaxValue` | `Decimal` |

LiteDB 5.0.21 already used `Int64` for mapper-written `UInt64` values, including
the unchecked negative bit pattern for values above `Int64.MaxValue`. The current
reader accepts those values so existing mapped documents still round-trip.

Direct `BsonValue` conversion in 5.0.21 used `Double`. Values above 2^53 may
therefore already be rounded in an existing file. Equality operations created
from a CLR `UInt64` probe both the current exact value and that legacy rounded
value. This applies to raw and typed lookup, LINQ and indexed equality, delete,
update, upsert, and uniqueness checks. Signed and fractional inputs do not opt
into this fallback.

The legacy `Double` representation of `UInt64.MaxValue` is exactly 2^64. It is
read deterministically as `UInt64.MaxValue`; negative, infinite, and larger
values throw `OverflowException`. Updates keep a legacy primary key in its
stored `Double` form so its data record and primary index remain aligned.
