# Mapping, serialization, and public contracts

Use this for mapper inference, BSON encodings, constructors, projections, and APIs.

## Preserve the selected contract

- Explicit mappings, custom serializers, constructors, and virtual mapper hooks
  take precedence over inferred shortcuts. Test them through public APIs, including
  projections and vector-scored projections, not only an internal helper.
- Distinguish the declared type from the runtime type. A dictionary may expose
  several generic views or an unrelated generic side store; the first generic
  interface found need not describe the non-generic `IDictionary` being enumerated.
  Preserve explicitly selected key/value contracts and historical erased-type
  behavior. Test reordered generic arguments, opaque interfaces, covariance,
  discriminator resolution, and custom key/value converters.
- Match inherited/explicit interface and abstract members by their actual slots
  or override identity. Same-named sibling members and hidden properties are not
  interchangeable. Reflection lookup needs a signature, and a hidden method is
  not an override. Preserve custom mapper choices, including choosing no ID.
- Constructor binding must preserve custom factories and virtual deserialization
  hooks. Do not remove fields or mutate the original document before a hook that
  previously saw them. Temporary constructor metadata must be invocation-scoped
  and cleaned on exceptions, reentrancy, and replacement factories.
- Preserve BSON container invariants. Case-sensitive CLR dictionaries/Expando
  objects may contain keys that collide in a BSON document; avoid silent overwrite
  or divergence between `_id`, stored data, and index keys.
- Use the database's owning mapper throughout its collections, queries, and
  FileStorage; do not silently switch to a global/unrelated mapper in a helper.
  `LiteDatabase` uses an explicitly supplied mapper as-is and clones
  `BsonMapper.Global` when none is supplied. Preserve that isolation and clone
  behavior when adding owned objects or transferring query objects between databases.

## Values, persistence, and API coverage

Check both round-trip values and BSON representation. Numeric tests should include
signed/unsigned extremes, values above `long.MaxValue`, fractions, NaN/infinities,
and mixed-type equality/order/hash laws. Legacy ID compatibility must cover lookup,
LINQ, delete, update/upsert, and index paths, including raw `BsonDocument` APIs.
One successful `FindById` does not establish compatibility.

Preserve byte order and buffer ownership in span/pooling changes. Exercise
contiguous and segmented reads/writes, short buffers, alignment, and supported
architectures. Read [compatibility](compatibility.md) before changing serialized
key spellings, dates, enum arrays, or numeric representations.

Connection-string changes need parse/format/reparse tests with equals signs,
quotes, trailing backslashes, significant whitespace, empty values, custom options,
and every recognized option. Do not turn malformed option strings into writable
filenames. Keep password exposure an explicit API decision. See
[connection-string parsing](../connection-string-parsing.md).

Check overload selection and source/binary compatibility for public API additions.
Arrays/lists may bind to a single-entity generic overload; new abstract interface
members can break third-party implementations. Preserve exception types/chains
and contextual diagnostics when changing error handling. See
[bulk overloads](../repository-bulk-overloads.md) and
[mapper diagnostics](../mapper-error-diagnostics.md).

When changing type-name binder restrictions, test nested generic/member/collection
construction too: rejecting only an outer `_type` name can leave the same unsafe
instantiation reachable through an allowed container.
