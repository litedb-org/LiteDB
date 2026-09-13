using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using LiteDB.Generated;

#nullable enable
namespace LiteDB.AotSmokeTests
{
    internal sealed class FailOnGenericConversionMapper : BsonMapper
    {
        [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Runtime model mapping is not trimming safe.")]
        public override BsonDocument ToDocument(Type type, object entity) => type == typeof(BsonDocument)
            ? (BsonDocument)entity
            : throw new InvalidOperationException($"Generated execution reached broad {nameof(ToDocument)} conversion for '{type}'.");

        [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Runtime model mapping is not trimming safe.")]
        [System.Diagnostics.CodeAnalysis.RequiresDynamicCode("Runtime type construction requires dynamic code.")]
        public override object ToObject(Type type, BsonDocument document) => type == typeof(BsonDocument)
            ? document
            : throw new InvalidOperationException($"Generated execution reached broad {nameof(ToObject)} conversion for '{type}'.");
    }

    [BsonSourceGenerated]
    public sealed record AotMutableRecord
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public class AotOverrideBase
    {
        [BsonId(false)]
        public virtual int OverrideId { get; set; }
    }

    public class AotOverrideMiddle : AotOverrideBase
    {
        public override int OverrideId { get; set; }
    }

    [BsonSourceGenerated]
    public sealed class AotOverrideRecord : AotOverrideMiddle
    {
        private int _overrideId;

        public override int OverrideId
        {
            get => _overrideId;
            set
            {
                _overrideId = value;
                SetterCalls++;
            }
        }

        public string Name { get; set; } = string.Empty;

        [BsonIgnore]
        public int SetterCalls { get; private set; }
    }

    [BsonSourceGenerated]
    public sealed class AotSimpleRecord : AotSimpleRecordBase
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public long Score { get; set; }

        public Dictionary<string, object> LegacyProbe { get; set; } = [];
    }

    // Keeps this retained manual Phase B smoke fixture outside automatic direct-map emission.
    public class AotSimpleRecordBase
    {
    }

    [BsonSourceGenerated]
    public sealed class AotListRecord
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public List<string>? Values { get; set; } = [];
    }

    [BsonSourceGenerated]
    public sealed class AotDynamicDictionaryRecord
    {
        public int Id { get; set; }
        public Dictionary<string, object?> Fields { get; set; } = [];
    }

    [BsonSourceGenerated]
    public sealed class AotStringArrayRecord
    {
        public int Id { get; set; }
        public string[]? StreamNames { get; set; }
    }

    [BsonSourceGenerated]
    public sealed class AotNullableScalarRecord
    {
        public int Id { get; set; }
        public int? ProcessId { get; set; }
        public bool? IsElevated { get; set; }
        public AotNativeScalarState? State { get; set; }
        public Guid? CorrelationId { get; set; }
        public DateTime? RecordedAt { get; set; }
    }

    public abstract class AotInheritedRecordBase
    {
        [BsonId(false)]
        public int BaseId { get; set; }

        [BsonField("base_name")]
        public string BaseName { get; set; } = string.Empty;

        public List<string> BaseTags { get; set; } = [];

        [BsonIgnore]
        public string? IgnoredBaseValue { get; set; }
    }

    [BsonSourceGenerated]
    public sealed class AotInheritedRecord : AotInheritedRecordBase
    {
        public string DerivedName { get; set; } = string.Empty;
        public string Fingerprint => string.Join("|", BaseName, DerivedName);
    }

    [BsonSourceGenerated]
    public sealed class AotPhaseCScalarRecord
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public int Score { get; set; }
    }

    [BsonSourceGenerated]
    public sealed class AotPhaseCScalarCompatibilityRecord
    {
        public int Id { get; set; }
        public bool BooleanValue { get; set; }
        public uint UnsignedInteger { get; set; }
        public long SignedLong { get; set; }
        public ulong UnsignedLong { get; set; }
        public decimal DecimalValue { get; set; }
        public AotNativeScalarState State { get; set; }
        public DateTime Timestamp { get; set; }
        public ObjectId ObjectId { get; set; } = ObjectId.Empty;
        public Guid CorrelationId { get; set; }
        public byte[] Payload { get; set; } = [];
        public string Name { get; set; } = string.Empty;
        public AotNativeScalarState? NullableState { get; set; }
        public byte[]? NullablePayload { get; set; }
    }

    [BsonSourceGenerated]
    public sealed class AotNativeScalarRecord
    {
        public int Id { get; set; }
        public bool BooleanValue { get; set; }
        public byte ByteValue { get; set; }
        public sbyte SignedByteValue { get; set; }
        public char Character { get; set; }
        public short SignedShort { get; set; }
        public ushort UnsignedShort { get; set; }
        public int SignedInteger { get; set; }
        public uint UnsignedInteger { get; set; }
        public long SignedLong { get; set; }
        public ulong UnsignedLong { get; set; }
        public float SingleValue { get; set; }
        public double DoubleValue { get; set; }
        public decimal DecimalValue { get; set; }
        public AotNativeScalarState State { get; set; }
        public ObjectId ObjectId { get; set; } = ObjectId.Empty;
        public DateTime Timestamp { get; set; }
        public DateTimeOffset TimestampWithOffset { get; set; }
        public byte[] Payload { get; set; } = [];
        public Guid CorrelationId { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public enum AotNativeScalarState
    {
        Unknown = 0,
        Captured = 17
    }

    [BsonSourceGenerated]
    public sealed class AotDateTimeOffsetRecord
    {
        public int Id { get; set; }
        public DateTimeOffset OccurredAt { get; set; }
        public DateTimeOffset? DeliveredAt { get; set; }
    }

    [BsonSourceGenerated]
    public sealed class AotDateTimeOffsetBoundaryRecord
    {
        public int Id { get; set; }
        public DateTimeOffset PositiveOffset { get; set; }
        public DateTimeOffset NegativeOffset { get; set; }
        public DateTimeOffset? NullableOffset { get; set; }
    }

    [BsonSourceGenerated]
    public sealed class AotNullableScalarBoundaryRecord
    {
        public int Id { get; set; }
        public short? SignedShort { get; set; }
        public ulong? UnsignedLong { get; set; }
        public double? Ratio { get; set; }
        public decimal? Amount { get; set; }
        public DateTimeOffset? TimestampWithOffset { get; set; }
    }

    public abstract class AotMultiLevelInheritedGrandparent
    {
        [BsonId(false)]
        public int RootId { get; set; }

        [BsonField("origin")]
        public string Origin { get; set; } = string.Empty;
    }

    public abstract class AotMultiLevelInheritedParent : AotMultiLevelInheritedGrandparent
    {
        public string ParentName { get; set; } = string.Empty;
        public List<string> Values { get; set; } = [];

        [BsonIgnore]
        public string? IgnoredParentValue { get; set; }
    }

    [BsonSourceGenerated]
    public sealed class AotMultiLevelInheritedRecord : AotMultiLevelInheritedParent
    {
        public string DerivedName { get; set; } = string.Empty;
        public string Fingerprint => string.Join("|", Origin, DerivedName, string.Join(",", Values));
        public int ValueCount => Values.Count;
    }

    internal sealed class UnsupportedAotDynamicDictionaryValue
    {
    }

    [BsonSourceGenerated]
    public sealed class AotSecondaryRecord
    {
        public int Id { get; set; }
        public string Description { get; set; } = string.Empty;
    }
}
