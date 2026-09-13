using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using LiteDB.Engine;
using LiteDB.Generated;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LiteDB.AotTests
{
    public sealed class UnsupportedExecutionRecord
    {
        public int Id { get; set; }
    }

    [BsonSourceGenerated]
    public sealed class PhaseCScalarRecord
    {
        public int Id { get; set; }

        public string? Name { get; set; }

        public int Score { get; set; }
    }

    public enum PhaseCScalarState
    {
        Ready = 1,
        Completed = 5
    }

    [BsonSourceGenerated]
    public sealed class PhaseCScalarCompatibilityRecord
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
        public PhaseCScalarState State { get; set; }
        public DateTime Timestamp { get; set; }
        public ObjectId ObjectId { get; set; } = LiteDB.ObjectId.Empty;
        public Guid CorrelationId { get; set; }
        public byte[] Payload { get; set; } = [];
        public string? Name { get; set; }
        public int? NullableInteger { get; set; }
        public PhaseCScalarState? NullableState { get; set; }
        public ObjectId? NullableObjectId { get; set; }
        public Guid? NullableCorrelationId { get; set; }
        public DateTime? NullableTimestamp { get; set; }
        public byte[]? NullablePayload { get; set; }
    }

    [BsonSourceGenerated]
    public sealed class PhaseCAttributedScalarRecord
    {
        public int Id { get; set; }

        [BsonField("score")]
        public int Score { get; set; }
    }

    [BsonSourceGenerated]
    public sealed class PhaseBGeneratedRecord : PhaseBGeneratedRecordBase
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public long Score { get; set; }

        public Dictionary<string, object?> LegacyProbe { get; set; } = [];
    }

    // Keeps the retained manual Phase B registry fixture outside automatic direct-map emission.
    public class PhaseBGeneratedRecordBase
    {
    }

    internal sealed class ThrowingConversionMapper : BsonMapper
    {
        public override BsonDocument ToDocument(Type type, object entity) =>
            throw new AssertFailedException($"Generated execution must not call {nameof(ToDocument)}.");

        public override object ToObject(Type type, BsonDocument document) =>
            throw new AssertFailedException($"Generated execution must not call {nameof(ToObject)}.");
    }

    [BsonSourceGenerated]
    public sealed record MutableGeneratedRecord
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    public class OverrideRecordBase
    {
        [BsonId(false)]
        public virtual int OverrideId { get; set; }

        [BsonField("stored_name")]
        public virtual string Name { get; set; } = string.Empty;

        [BsonIgnore]
        public virtual string? Ignored { get; set; }
    }

    public class OverrideRecordMiddle : OverrideRecordBase
    {
        public override int OverrideId { get; set; }
        public override string Name { get; set; } = string.Empty;
        public override string? Ignored { get; set; }
    }

    [BsonSourceGenerated]
    public sealed class OverrideRecord : OverrideRecordMiddle
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

        public override string Name { get; set; } = string.Empty;
        public override string? Ignored { get; set; }

        [BsonIgnore]
        public int SetterCalls { get; private set; }
    }

    [BsonSourceGenerated]
    public sealed class GeneratedRecord
    {
        public int Id { get; set; }

        [BsonField("name")]
        public string Name { get; set; } = string.Empty;

        public List<string> Values { get; set; } = [];

        [BsonIgnore]
        public string? Ignored { get; set; }
    }

    [BsonSourceGenerated]
    public sealed class NullableListRecord
    {
        public int Id { get; set; }

        public List<string>? Values { get; set; }
    }

    [BsonSourceGenerated]
    public sealed class ConventionIdRecord
    {
        public int ConventionIdRecordId { get; set; }

        [BsonField(Name = "stored_name")]
        public string Name { get; set; } = string.Empty;

        [BsonIgnore]
        public string? IgnoredText { get; set; }

        [BsonIgnore]
        public int IgnoredNumber { get; set; }
    }

    [BsonSourceGenerated]
    public sealed class ScalarRecord
    {
        public int Id { get; set; }
        public bool Enabled { get; set; }
        public int Count { get; set; }
        public long Total { get; set; }
        public double Ratio { get; set; }
        public decimal Amount { get; set; }
        public DateTime Timestamp { get; set; }
        public Guid CorrelationId { get; set; }
        public byte[] Payload { get; set; } = [];
    }

    [BsonSourceGenerated]
    public sealed class NullableScalarRecord
    {
        public int Id { get; set; }
        public int? ProcessId { get; set; }
        public bool? IsElevated { get; set; }
        public NativeScalarState? State { get; set; }
        public Guid? CorrelationId { get; set; }
        public DateTime? RecordedAt { get; set; }
    }

    [BsonSourceGenerated]
    public sealed class DynamicDictionaryRecord
    {
        public int Id { get; set; }
        public Dictionary<string, object?> Fields { get; set; } = [];
    }

    internal sealed class UnsupportedDynamicDictionaryValue
    {
    }

    [BsonSourceGenerated]
    public sealed class StringArrayRecord
    {
        public int Id { get; set; }
        public string[]? StreamNames { get; set; }
    }

    [BsonSourceGenerated]
    public sealed class ComputedRecord
    {
        public int Id { get; set; }
        public string NodeType { get; set; } = string.Empty;
        public string ContentHash { get; set; } = string.Empty;
        public string Fingerprint => string.Join("|", NodeType, ContentHash);
    }

    public abstract class InheritedRecordBase
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
    public sealed class InheritedRecord : InheritedRecordBase
    {
        public string DerivedName { get; set; } = string.Empty;
    }

    [BsonSourceGenerated]
    public sealed class NativeScalarRecord
    {
        public int Id { get; set; }
        public byte ByteValue { get; set; }
        public sbyte SignedByteValue { get; set; }
        public char Character { get; set; }
        public short SignedShort { get; set; }
        public ushort UnsignedShort { get; set; }
        public uint UnsignedInteger { get; set; }
        public ulong UnsignedLong { get; set; }
        public float SingleValue { get; set; }
        public NativeScalarState State { get; set; }
        public ObjectId ObjectId { get; set; } = ObjectId.Empty;
        public DateTime Timestamp { get; set; }
        public DateTimeOffset TimestampWithOffset { get; set; }
        public byte[] Payload { get; set; } = [];
        public Guid CorrelationId { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public enum NativeScalarState
    {
        Unknown = 0,
        Captured = 17
    }

    [BsonSourceGenerated]
    public sealed class DateTimeOffsetRecord
    {
        public int Id { get; set; }
        public DateTimeOffset OccurredAt { get; set; }
        public DateTimeOffset? DeliveredAt { get; set; }
    }

    [BsonSourceGenerated]
    public sealed class DateTimeOffsetBoundaryRecord
    {
        public int Id { get; set; }
        public DateTimeOffset PositiveOffset { get; set; }
        public DateTimeOffset NegativeOffset { get; set; }
        public DateTimeOffset? NullableOffset { get; set; }
        public DateTimeOffset Minimum { get; set; }
        public DateTimeOffset Maximum { get; set; }
    }

    [BsonSourceGenerated]
    public sealed class NullableScalarBoundaryRecord
    {
        public int Id { get; set; }
        public short? SignedShort { get; set; }
        public ulong? UnsignedLong { get; set; }
        public double? Ratio { get; set; }
        public decimal? Amount { get; set; }
        public DateTimeOffset? TimestampWithOffset { get; set; }
    }

    public abstract class MultiLevelInheritedGrandparent
    {
        [BsonId(false)]
        public int RootId { get; set; }

        [BsonField("origin")]
        public string Origin { get; set; } = string.Empty;
    }

    public abstract class MultiLevelInheritedParent : MultiLevelInheritedGrandparent
    {
        public string ParentName { get; set; } = string.Empty;

        [BsonIgnore]
        public string? IgnoredParentValue { get; set; }
    }

    [BsonSourceGenerated]
    public sealed class MultiLevelInheritedRecord : MultiLevelInheritedParent
    {
        public string DerivedName { get; set; } = string.Empty;
    }

    [BsonSourceGenerated]
    public sealed class ComputedProjectionRecord
    {
        public int Id { get; set; }
        public string NodeType { get; set; } = string.Empty;
        public List<string> Values { get; set; } = [];
        public string Fingerprint => string.Join("|", NodeType, string.Join("|", Values));
        public int ValueCount => Values.Count;
        public string ValueSummary => string.Join(",", Values);
    }

    [BsonSourceGenerated]
    public sealed class SecondaryRecord
    {
        public int Id { get; set; }
        public string Description { get; set; } = string.Empty;
    }

    [BsonSourceGenerated]
    public sealed class NoIdRecord
    {
        public string Name { get; set; } = string.Empty;
    }

    [BsonSourceGenerated]
    public sealed class ExplicitIdRecord
    {
        [BsonId(false)]
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }
}
