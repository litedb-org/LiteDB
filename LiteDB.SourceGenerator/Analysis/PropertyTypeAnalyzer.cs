using LiteDB.SourceGenerator.Models;

using Microsoft.CodeAnalysis;

namespace LiteDB.SourceGenerator.Analysis;

internal static class PropertyTypeAnalyzer
{
    public static PropertyKind GetPropertyKind(ITypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Enum)
        {
            return PropertyKind.Scalar;
        }

        if (type is IArrayTypeSymbol arrayType &&
            arrayType.Rank == 1 &&
            arrayType.ElementType.SpecialType == SpecialType.System_String)
        {
            return PropertyKind.StringArray;
        }

        if (type.SpecialType is
            SpecialType.System_Boolean or
            SpecialType.System_Byte or
            SpecialType.System_SByte or
            SpecialType.System_Char or
            SpecialType.System_Int16 or
            SpecialType.System_UInt16 or
            SpecialType.System_Int32 or
            SpecialType.System_UInt32 or
            SpecialType.System_Int64 or
            SpecialType.System_UInt64 or
            SpecialType.System_Single or
            SpecialType.System_Double or
            SpecialType.System_Decimal or
            SpecialType.System_String)
        {
            return PropertyKind.Scalar;
        }

        if (type is IArrayTypeSymbol { Rank: 1, ElementType.SpecialType: SpecialType.System_Byte })
        {
            return PropertyKind.Scalar;
        }

        if (type is INamedTypeSymbol namedType)
        {
            if (IsStringObjectDictionary(namedType))
            {
                return PropertyKind.DynamicDictionary;
            }

            if (namedType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
                namedType.TypeArguments.Length == 1)
            {
                return GetPropertyKind(namedType.TypeArguments[0]) switch
                {
                    PropertyKind.Scalar => PropertyKind.Scalar,
                    PropertyKind.DateTimeOffset => PropertyKind.NullableDateTimeOffset,
                    _ => PropertyKind.Unsupported
                };
            }

            var metadataName = namedType.WithNullableAnnotation(NullableAnnotation.None).ToDisplayString();
            if (metadataName is "System.DateTime" or "System.Guid" or "LiteDB.ObjectId")
            {
                return PropertyKind.Scalar;
            }

            if (namedType.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.List<T>" &&
                namedType.TypeArguments.Length == 1 &&
                namedType.TypeArguments[0].SpecialType == SpecialType.System_String)
            {
                return PropertyKind.StringList;
            }

            if (metadataName == "System.DateTimeOffset")
            {
                return PropertyKind.DateTimeOffset;
            }
        }

        return PropertyKind.Unsupported;
    }

    public static ScalarConversionKind GetScalarConversionKind(ITypeSymbol type, out bool isNullableScalar)
    {
        isNullableScalar = false;
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T, TypeArguments.Length: 1 } nullableType)
        {
            isNullableScalar = true;
            type = nullableType.TypeArguments[0];
        }

        if (type.TypeKind == TypeKind.Enum)
        {
            return ScalarConversionKind.Enum;
        }

        if (type is IArrayTypeSymbol { Rank: 1, ElementType.SpecialType: SpecialType.System_Byte })
        {
            return ScalarConversionKind.ByteArray;
        }

        var specialKind = GetSpecialTypeConversionKind(type.SpecialType);
        if (specialKind != ScalarConversionKind.None)
        {
            return specialKind;
        }

        return type.WithNullableAnnotation(NullableAnnotation.None).ToDisplayString() switch
        {
            "System.DateTime" => ScalarConversionKind.DateTime,
            "System.DateTimeOffset" => ScalarConversionKind.DateTimeOffset,
            "System.Guid" => ScalarConversionKind.Guid,
            "LiteDB.ObjectId" => ScalarConversionKind.ObjectId,
            _ => ScalarConversionKind.None
        };
    }

    public static ScalarConversionKind GetEnumUnderlyingConversionKind(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T, TypeArguments.Length: 1 } nullableType)
        {
            type = nullableType.TypeArguments[0];
        }

        return type is INamedTypeSymbol { TypeKind: TypeKind.Enum, EnumUnderlyingType: { } underlyingType }
            ? GetSpecialTypeConversionKind(underlyingType.SpecialType)
            : ScalarConversionKind.None;
    }

    private static bool IsStringObjectDictionary(INamedTypeSymbol type)
    {
        var definition = type.OriginalDefinition;
        return definition.MetadataName == "Dictionary`2" &&
            definition.ContainingNamespace.ToDisplayString() == "System.Collections.Generic" &&
            type.TypeArguments.Length == 2 &&
            type.TypeArguments[0].SpecialType == SpecialType.System_String &&
            type.TypeArguments[1].SpecialType == SpecialType.System_Object;
    }

    private static ScalarConversionKind GetSpecialTypeConversionKind(SpecialType specialType) => specialType switch
    {
        SpecialType.System_Boolean => ScalarConversionKind.Boolean,
        SpecialType.System_Byte => ScalarConversionKind.Byte,
        SpecialType.System_SByte => ScalarConversionKind.SByte,
        SpecialType.System_Char => ScalarConversionKind.Char,
        SpecialType.System_Int16 => ScalarConversionKind.Int16,
        SpecialType.System_UInt16 => ScalarConversionKind.UInt16,
        SpecialType.System_Int32 => ScalarConversionKind.Int32,
        SpecialType.System_UInt32 => ScalarConversionKind.UInt32,
        SpecialType.System_Int64 => ScalarConversionKind.Int64,
        SpecialType.System_UInt64 => ScalarConversionKind.UInt64,
        SpecialType.System_Single => ScalarConversionKind.Single,
        SpecialType.System_Double => ScalarConversionKind.Double,
        SpecialType.System_Decimal => ScalarConversionKind.Decimal,
        SpecialType.System_String => ScalarConversionKind.String,
        _ => ScalarConversionKind.None
    };
}
