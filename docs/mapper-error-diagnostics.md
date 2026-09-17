# Mapper errors in v6

`BsonMapper.ToDocument` now throws `LiteException` with the runtime entity type
when a root value serializes as an array, scalar, or null. It previously returned
null for those values, which caused later collection writes to fail without
context. Use `Serialize` for non-document BSON values or register a serializer
that returns a document.

Failures in mapped getters and member serializers identify the entity/member at
each nesting level. The original exception remains in the inner-exception chain;
existing `LiteException` error codes are preserved. Mapping initialization also
preserves its original exception, including messages with literal braces.
Missing constructor parameter names now identify the affected type and explain
how to preserve metadata for trimmed applications or register a constructor.

Successful custom serializers and the existing runtime-metadata exclusion remain
supported. An ordinary exception's data can still serialize successfully; it is
not rejected solely because its type derives from `Exception`.
