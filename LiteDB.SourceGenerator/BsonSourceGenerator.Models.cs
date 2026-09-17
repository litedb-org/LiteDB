using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace LiteDB.SourceGenerator;

public sealed partial class BsonSourceGenerator
{
    private sealed record ModelResult(
        ModelDescriptor? Model,
        string TypeName,
        DiagnosticLocationDescriptor? DiagnosticLocation,
        DiagnosticKind DiagnosticKind,
        string? Error)
    {
        public static ModelResult Supported(ModelDescriptor model) => new(model, model.TypeName, null, DiagnosticKind.None, null);
        public static ModelResult InvalidModel(string typeName, DiagnosticLocationDescriptor diagnosticLocation, string error) =>
            new(null, typeName, diagnosticLocation, DiagnosticKind.InvalidModel, error);
        public static ModelResult InvalidProperty(string typeName, DiagnosticLocationDescriptor diagnosticLocation, string error) =>
            new(null, typeName, diagnosticLocation, DiagnosticKind.InvalidProperty, error);
        public static ModelResult MappingConflict(string typeName, DiagnosticLocationDescriptor diagnosticLocation, string error) =>
            new(null, typeName, diagnosticLocation, DiagnosticKind.MappingConflict, error);
    }

    private enum DiagnosticKind
    {
        None,
        InvalidModel,
        InvalidProperty,
        MappingConflict
    }

    private sealed record DiagnosticLocationDescriptor(
        string FilePath,
        TextSpan SourceSpan,
        LinePositionSpan LineSpan)
    {
        public Location Create() => Location.Create(FilePath, SourceSpan, LineSpan);
    }

    private sealed record ModelDescriptor(
        string TypeName,
        EquatableArray<PropertyDescriptor> Properties,
        bool CanEmitExecutionMap);

    private readonly struct EquatableArray<T> : IReadOnlyList<T>, IEquatable<EquatableArray<T>>
    {
        private readonly T[]? _items;

        public EquatableArray(IEnumerable<T> items)
        {
            _items = items.ToArray();
        }

        public int Count => Items.Length;

        public T this[int index] => Items[index];

        public bool Equals(EquatableArray<T> other)
        {
            var items = Items;
            var otherItems = other.Items;
            return items.Length == otherItems.Length && items.SequenceEqual(otherItems);
        }

        public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var comparer = EqualityComparer<T>.Default;
                var hash = 17;
                foreach (var item in Items)
                {
                    hash = (hash * 31) ^ (item is null ? 0 : comparer.GetHashCode(item));
                }

                return hash;
            }
        }

        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)Items).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => Items.GetEnumerator();

        private T[] Items => _items ?? Array.Empty<T>();
    }

    private sealed record PropertyDescriptor(
        string Name,
        string Identifier,
        string TypeName,
        string FieldName,
        PropertyKind Kind,
        ScalarConversionKind ScalarKind,
        ScalarConversionKind EnumUnderlyingKind,
        string ScalarTypeName,
        bool IsNullableScalar,
        bool HasBsonId,
        bool AutoId,
        bool IsId);

    private enum ScalarConversionKind
    {
        None,
        Boolean,
        Byte,
        SByte,
        Char,
        Int16,
        UInt16,
        Int32,
        UInt32,
        Int64,
        UInt64,
        Single,
        Double,
        Decimal,
        String,
        ByteArray,
        DateTime,
        DateTimeOffset,
        Guid,
        ObjectId,
        Enum
    }

    private enum PropertyKind
    {
        Scalar,
        StringList,
        StringArray,
        DynamicDictionary,
        DateTimeOffset,
        NullableDateTimeOffset,
        Unsupported
    }
}
