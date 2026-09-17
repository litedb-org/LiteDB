using System;
using System.Text;

using LiteDB.SourceGenerator.Models;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace LiteDB.SourceGenerator.Emission;

internal static class GeneratedExecutionEmitter
{
    public static void AppendExecutionMapFactory(StringBuilder source, ModelDescriptor model, int index)
    {
        source.AppendLine();
        source.Append("        private static global::LiteDB.GeneratedEntityMap<").Append(model.TypeName).Append("> CreateExecutionMap").Append(index).AppendLine("()");
        source.AppendLine("        {");
        source.Append("            return new global::LiteDB.GeneratedEntityMap<").Append(model.TypeName).Append(">(SerializeExecution").Append(index).Append(", DeserializeExecution").Append(index).AppendLine(");");
        source.AppendLine("        }");
        source.AppendLine();
        source.Append("        private static global::LiteDB.BsonDocument SerializeExecution").Append(index).Append("(").Append(model.TypeName).AppendLine(" entity, global::LiteDB.GeneratedExecutionOptions options)");
        source.AppendLine("        {");
        source.AppendLine("            var document = new global::LiteDB.BsonDocument();");

        foreach (var property in model.Properties)
        {
            AppendSerializeExecutionProperty(source, property, SymbolDisplay.FormatLiteral(property.FieldName, true));
        }

        source.AppendLine("            return document;");
        source.AppendLine("        }");
        source.AppendLine();
        source.Append("        private static ").Append(model.TypeName).Append(" DeserializeExecution").Append(index).Append("(global::LiteDB.BsonDocument document, global::LiteDB.GeneratedExecutionOptions _)").AppendLine();
        source.AppendLine("        {");
        source.Append("            var entity = new ").Append(model.TypeName).AppendLine("();");

        for (var propertyIndex = 0; propertyIndex < model.Properties.Count; propertyIndex++)
        {
            var property = model.Properties[propertyIndex];
            AppendDeserializeExecutionProperty(source, property, SymbolDisplay.FormatLiteral(property.FieldName, true), propertyIndex);
        }

        source.AppendLine("            return entity;");
        source.AppendLine("        }");
    }

    private static void AppendSerializeExecutionProperty(StringBuilder source, PropertyDescriptor property, string fieldLiteral)
    {
        var access = "entity." + property.Identifier;
        if (property.Kind == PropertyKind.DynamicDictionary)
        {
            source.Append("            if (").Append(access).AppendLine(" is null)");
            source.AppendLine("            {");
            source.Append("                if (options.SerializeNullValues) document[").Append(fieldLiteral).AppendLine("] = global::LiteDB.BsonValue.Null;");
            source.AppendLine("            }");
            source.AppendLine("            else");
            source.AppendLine("            {");
            source.Append("                document[").Append(fieldLiteral).Append("] = SerializeDynamicDictionaryCore(").Append(access).AppendLine(", options, 1);");
            source.AppendLine("            }");
            return;
        }

        if (property.Kind is PropertyKind.StringList or PropertyKind.StringArray)
        {
            source.Append("            if (").Append(access).AppendLine(" is null)");
            source.AppendLine("            {");
            source.Append("                if (options.SerializeNullValues) document[").Append(fieldLiteral).AppendLine("] = global::LiteDB.BsonValue.Null;");
            source.AppendLine("            }");
            source.AppendLine("            else");
            source.AppendLine("            {");
            source.AppendLine("                var array = new global::LiteDB.BsonArray();");
            source.Append("                foreach (var item in ").Append(access).AppendLine(")");
            source.AppendLine("                {");
            source.AppendLine("                    if (item is null)");
            source.AppendLine("                    {");
            source.AppendLine("                        array.Add(global::LiteDB.BsonValue.Null);");
            source.AppendLine("                    }");
            source.AppendLine("                    else");
            source.AppendLine("                    {");
            source.AppendLine("                        var text = options.TrimWhitespace ? item.Trim() : item;");
            source.AppendLine("                        array.Add(options.EmptyStringToNull && text.Length == 0 ? global::LiteDB.BsonValue.Null : new global::LiteDB.BsonValue(text));");
            source.AppendLine("                    }");
            source.AppendLine("                }");
            source.Append("                document[").Append(fieldLiteral).AppendLine("] = array;");
            source.AppendLine("            }");
            return;
        }

        if (property.ScalarKind == ScalarConversionKind.String)
        {
            source.Append("            if (").Append(access).AppendLine(" is null)");
            source.AppendLine("            {");
            if (property.IsId)
            {
                source.Append("                document[").Append(fieldLiteral).AppendLine("] = global::LiteDB.BsonValue.Null;");
            }
            else
            {
                source.Append("                if (options.SerializeNullValues) document[").Append(fieldLiteral).AppendLine("] = global::LiteDB.BsonValue.Null;");
            }
            source.AppendLine("            }");
            source.AppendLine("            else");
            source.AppendLine("            {");
            source.Append("                var text = options.TrimWhitespace ? ").Append(access).Append(".Trim() : ").Append(access).AppendLine(";");
            source.Append("                document[").Append(fieldLiteral).Append("] = options.EmptyStringToNull && text.Length == 0 ? global::LiteDB.BsonValue.Null : new global::LiteDB.BsonValue(text);").AppendLine();
            source.AppendLine("            }");
            return;
        }

        if (property.IsNullableScalar || IsReferenceScalar(property.ScalarKind))
        {
            source.Append("            if (").Append(access).AppendLine(" is null)");
            source.AppendLine("            {");
            if (property.IsId)
            {
                source.Append("                document[").Append(fieldLiteral).AppendLine("] = global::LiteDB.BsonValue.Null;");
            }
            else
            {
                source.Append("                if (options.SerializeNullValues) document[").Append(fieldLiteral).AppendLine("] = global::LiteDB.BsonValue.Null;");
            }
            source.AppendLine("            }");
            source.AppendLine("            else");
            source.AppendLine("            {");
            var value = property.IsNullableScalar ? access + ".Value" : access;
            source.Append("                document[").Append(fieldLiteral).Append("] = ").Append(GetSerializeExpression(property, value)).AppendLine(";");
            source.AppendLine("            }");
            return;
        }

        source.Append("            document[").Append(fieldLiteral).Append("] = ").Append(GetSerializeExpression(property, access)).AppendLine(";");
    }

    private static void AppendDeserializeExecutionProperty(StringBuilder source, PropertyDescriptor property, string fieldLiteral, int propertyIndex)
    {
        var value = "value" + propertyIndex;
        source.Append("            if (document.TryGetValue(").Append(fieldLiteral).Append(", out var ").Append(value).AppendLine(") && " + value + ".IsNull == false)");
        source.AppendLine("            {");
        if (property.Kind == PropertyKind.DynamicDictionary)
        {
            source.Append("                entity.").Append(property.Identifier).Append(" = DeserializeDynamicDictionary(").Append(value).AppendLine(")!;");
        }
        else if (property.Kind == PropertyKind.StringList)
        {
            source.Append("                var result").Append(propertyIndex).Append(" = new global::System.Collections.Generic.List<string>(").Append(value).AppendLine(".AsArray.Count);");
            source.Append("                foreach (var item in ").Append(value).AppendLine(".AsArray)");
            source.AppendLine("                {");
            source.Append("                    result").Append(propertyIndex).AppendLine(".Add(item.IsNull ? null! : item.AsString);");
            source.AppendLine("                }");
            source.Append("                entity.").Append(property.Identifier).Append(" = result").Append(propertyIndex).AppendLine(";");
        }
        else if (property.Kind == PropertyKind.StringArray)
        {
            source.Append("                var array").Append(propertyIndex).Append(" = ").Append(value).AppendLine(".AsArray;");
            source.Append("                var result").Append(propertyIndex).Append(" = new string[array").Append(propertyIndex).AppendLine(".Count];");
            source.Append("                for (var index = 0; index < array").Append(propertyIndex).AppendLine(".Count; index++)");
            source.AppendLine("                {");
            source.Append("                    var item = array").Append(propertyIndex).AppendLine("[index];");
            source.Append("                    result").Append(propertyIndex).AppendLine("[index] = item.IsNull ? null! : item.AsString;");
            source.AppendLine("                }");
            source.Append("                entity.").Append(property.Identifier).Append(" = result").Append(propertyIndex).AppendLine(";");
        }
        else
        {
            source.Append("                entity.").Append(property.Identifier).Append(" = ").Append(GetDeserializeExpression(property, value)).AppendLine(";");
        }
        source.AppendLine("            }");
        if (property.IsNullableScalar || IsReferenceScalar(property.ScalarKind) ||
            property.Kind is PropertyKind.StringList or PropertyKind.StringArray or PropertyKind.DynamicDictionary)
        {
            source.Append("            else if (document.TryGetValue(").Append(fieldLiteral).Append(", out ").Append(value).Append(") && ").Append(value).AppendLine(".IsNull)");
            source.AppendLine("            {");
            source.Append("                entity.").Append(property.Identifier).AppendLine(" = null!;");
            source.AppendLine("            }");
        }
    }

    private static bool IsReferenceScalar(ScalarConversionKind kind)
    {
        return kind is ScalarConversionKind.String or ScalarConversionKind.ByteArray or ScalarConversionKind.ObjectId;
    }

    private static string GetSerializeExpression(PropertyDescriptor property, string value)
    {
        return property.ScalarKind switch
        {
            ScalarConversionKind.Boolean => "new global::LiteDB.BsonValue(" + value + ")",
            ScalarConversionKind.Byte => "new global::LiteDB.BsonValue((int)" + value + ")",
            ScalarConversionKind.SByte => "new global::LiteDB.BsonValue((int)" + value + ")",
            ScalarConversionKind.Char => "new global::LiteDB.BsonValue(" + value + ".ToString())",
            ScalarConversionKind.Int16 => "new global::LiteDB.BsonValue((int)" + value + ")",
            ScalarConversionKind.UInt16 => "new global::LiteDB.BsonValue((int)" + value + ")",
            ScalarConversionKind.Int32 => "new global::LiteDB.BsonValue(" + value + ")",
            ScalarConversionKind.UInt32 => "new global::LiteDB.BsonValue((long)" + value + ")",
            ScalarConversionKind.Int64 => "new global::LiteDB.BsonValue(" + value + ")",
            ScalarConversionKind.UInt64 => "new global::LiteDB.BsonValue(unchecked((long)" + value + "))",
            ScalarConversionKind.Single => "new global::LiteDB.BsonValue((double)" + value + ")",
            ScalarConversionKind.Double => "new global::LiteDB.BsonValue(" + value + ")",
            ScalarConversionKind.Decimal => "new global::LiteDB.BsonValue(" + value + ")",
            ScalarConversionKind.ByteArray => "new global::LiteDB.BsonValue(" + value + ")",
            ScalarConversionKind.DateTime => "new global::LiteDB.BsonValue(" + value + ")",
            ScalarConversionKind.DateTimeOffset => "SerializeDateTimeOffset(" + value + ")",
            ScalarConversionKind.Guid => "new global::LiteDB.BsonValue(" + value + ")",
            ScalarConversionKind.ObjectId => "new global::LiteDB.BsonValue(" + value + ")",
            ScalarConversionKind.Enum => GetSerializeEnumExpression(property, value),
            _ => throw new InvalidOperationException("Unsupported generated execution scalar conversion.")
        };
    }

    private static string GetDeserializeExpression(PropertyDescriptor property, string value)
    {
        return property.ScalarKind switch
        {
            ScalarConversionKind.Boolean => value + ".AsBoolean",
            ScalarConversionKind.Byte => "(byte)" + value + ".AsInt32",
            ScalarConversionKind.SByte => "(sbyte)" + value + ".AsInt32",
            ScalarConversionKind.Char => value + ".AsString[0]",
            ScalarConversionKind.Int16 => "(short)" + value + ".AsInt32",
            ScalarConversionKind.UInt16 => "(ushort)" + value + ".AsInt32",
            ScalarConversionKind.Int32 => value + ".AsInt32",
            ScalarConversionKind.UInt32 => "(uint)" + value + ".AsInt64",
            ScalarConversionKind.Int64 => value + ".AsInt64",
            ScalarConversionKind.UInt64 => "unchecked((global::System.UInt64)" + value + ".AsInt64)",
            ScalarConversionKind.Single => "(float)" + value + ".AsDouble",
            ScalarConversionKind.Double => value + ".AsDouble",
            ScalarConversionKind.Decimal => value + ".AsDecimal",
            ScalarConversionKind.String => value + ".AsString",
            ScalarConversionKind.ByteArray => value + ".AsBinary",
            ScalarConversionKind.DateTime => value + ".AsDateTime",
            ScalarConversionKind.DateTimeOffset => "(global::System.DateTimeOffset)DeserializeDateTimeOffset(" + value + ")",
            ScalarConversionKind.Guid => value + ".AsGuid",
            ScalarConversionKind.ObjectId => value + ".AsObjectId",
            ScalarConversionKind.Enum => GetDeserializeEnumExpression(property, value),
            _ => throw new InvalidOperationException("Unsupported generated execution scalar conversion.")
        };
    }

    private static string GetSerializeEnumExpression(PropertyDescriptor property, string value)
    {
        var integerExpression = property.EnumUnderlyingKind switch
        {
            ScalarConversionKind.UInt32 or ScalarConversionKind.Int64 =>
                "new global::LiteDB.BsonValue((long)" + value + ")",
            ScalarConversionKind.UInt64 =>
                "new global::LiteDB.BsonValue(unchecked((long)" + value + "))",
            ScalarConversionKind.Byte or ScalarConversionKind.SByte or ScalarConversionKind.Int16 or
                ScalarConversionKind.UInt16 or ScalarConversionKind.Int32 =>
                "new global::LiteDB.BsonValue((int)" + value + ")",
            _ => throw new InvalidOperationException("Unsupported generated enum underlying type.")
        };

        return "options.EnumAsInteger ? " + integerExpression +
            " : new global::LiteDB.BsonValue(" + value + ".ToString())";
    }

    private static string GetDeserializeEnumExpression(PropertyDescriptor property, string value)
    {
        var integerExpression = property.EnumUnderlyingKind switch
        {
            ScalarConversionKind.UInt32 or ScalarConversionKind.Int64 or ScalarConversionKind.UInt64 =>
                "unchecked((" + property.ScalarTypeName + ")" + value + ".AsInt64)",
            ScalarConversionKind.Byte or ScalarConversionKind.SByte or ScalarConversionKind.Int16 or
                ScalarConversionKind.UInt16 or ScalarConversionKind.Int32 =>
                "(" + property.ScalarTypeName + ")" + value + ".AsInt32",
            _ => throw new InvalidOperationException("Unsupported generated enum underlying type.")
        };

        return value + ".IsString ? global::System.Enum.Parse<" + property.ScalarTypeName + ">(" + value +
            ".AsString) : " + integerExpression;
    }

}
