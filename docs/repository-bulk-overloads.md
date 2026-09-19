# Repository bulk overloads in v6

`LiteRepository` and `ILiteRepository` now expose array and `List<T>` overloads
for `Insert`, `Update`, and `Upsert`. For example, `repo.Insert(items)` where
`items` is an `Item[]` now inserts individual `Item` documents and returns their
count. Previously, generic inference selected the single-entity overload with
`T = Item[]`, which failed during serialization.

All six overloads delegate to the existing `IEnumerable<T>` implementations and
preserve `collectionName`. Insert returns the inserted count, Update the updated
count, and Upsert the count of newly inserted documents. Ordinary single-entity
calls keep their ID or Boolean return values.

This is an intentional v6 source and binary interface change. Custom
`ILiteRepository` implementations must add the six overloads and be recompiled;
applications must rebuild affected call sites to select the new overloads.
Implement each overload by casting its argument to `IEnumerable<T>` and invoking
the existing bulk method with the same collection name. Default interface method
bodies cannot provide compatibility on the supported .NET Framework consumers.

For other collection types, use an explicit element type, such as
`repo.Insert<Item>(items)`, or pass a variable typed as `IEnumerable<Item>`.
Existing binaries continue to target their original overload and should be
recompiled rather than relying on a runtime change in generic inference.
