using System;
using System.Linq.Expressions;

namespace LiteDB
{
    internal static partial class ExpressionInterpreter
    {
        private static object EvaluateUnary(UnaryExpression node, object value)
        {
            if (node.IsLiftedToNull && value == null) return null;
            if (node.Method != null && node.IsLifted && value == null)
                throw new InvalidOperationException("Nullable object must have a value.");
            if (node.Method != null) return Invoke(node.Method, null, new[] { value });
            if (node.NodeType == ExpressionType.TypeAs)
                return value == null || node.Type.IsInstanceOfType(value) ? value : null;
            if (node.NodeType == ExpressionType.Convert || node.NodeType == ExpressionType.ConvertChecked)
            {
                if (value == null)
                {
                    if (!node.Type.IsValueType || Nullable.GetUnderlyingType(node.Type) != null) return null;
                    if (Nullable.GetUnderlyingType(node.Operand.Type) != null)
                        throw new InvalidOperationException("Nullable object must have a value.");
                    throw new NullReferenceException();
                }
                if (node.Type.IsInstanceOfType(value)) return value;
                var source = Nullable.GetUnderlyingType(node.Operand.Type) ?? node.Operand.Type;
                var target = Nullable.GetUnderlyingType(node.Type) ?? node.Type;
                // Reference/unboxing casts must not turn a boxed int into a long.
                if (!source.IsValueType || !target.IsValueType) throw new InvalidCastException();
                if (source.IsEnum) value = Convert.ChangeType(value, Enum.GetUnderlyingType(source));
                var converted = ConvertNumeric(value, target.IsEnum ? Enum.GetUnderlyingType(target) : target,
                    node.NodeType == ExpressionType.ConvertChecked);
                return target.IsEnum ? Enum.ToObject(target, converted) : converted;
            }
            if (node.NodeType == ExpressionType.UnaryPlus) return value;
            var check = node.NodeType == ExpressionType.NegateChecked;
            if (node.NodeType == ExpressionType.Not)
            {
                switch (value)
                {
                    case bool boolean: return !boolean;
                    case int integer: return ~integer;
                    case long integer: return ~integer;
                    case uint integer: return ~integer;
                    case ulong integer: return ~integer;
                    default: throw Unsupported(node);
                }
            }
            switch (value)
            {
                case int integer: return check ? checked(-integer) : unchecked(-integer);
                case long integer: return check ? checked(-integer) : unchecked(-integer);
                case float number: return -number;
                case double number: return -number;
                default: throw Unsupported(node);
            }
        }

        private static object ConvertNumeric(object value, Type target, bool check)
        {
            switch (value)
            {
                case sbyte number: return CastNumber((long)number, target, check);
                case byte number: return CastNumber((long)number, target, check);
                case short number: return CastNumber((long)number, target, check);
                case ushort number: return CastNumber((long)number, target, check);
                case char number: return CastNumber((long)number, target, check);
                case int number: return CastNumber((long)number, target, check);
                case uint number: return CastNumber((long)number, target, check);
                case long number: return CastNumber(number, target, check);
                case ulong number: return CastNumber(number, target, check);
                case float number: return CastNumber((double)number, target, check);
                case double number: return CastNumber(number, target, check);
                default: throw new NotSupportedException("Numeric conversion is not supported by the AOT interpreter.");
            }
        }

        private static object CastNumber(long value, Type target, bool check)
        {
            switch (Type.GetTypeCode(target))
            {
                case TypeCode.SByte: return check ? checked((sbyte)value) : unchecked((sbyte)value);
                case TypeCode.Byte: return check ? checked((byte)value) : unchecked((byte)value);
                case TypeCode.Int16: return check ? checked((short)value) : unchecked((short)value);
                case TypeCode.UInt16: return check ? checked((ushort)value) : unchecked((ushort)value);
                case TypeCode.Char: return check ? checked((char)value) : unchecked((char)value);
                case TypeCode.Int32: return check ? checked((int)value) : unchecked((int)value);
                case TypeCode.UInt32: return check ? checked((uint)value) : unchecked((uint)value);
                case TypeCode.Int64: return check ? checked((long)value) : unchecked((long)value);
                case TypeCode.UInt64: return check ? checked((ulong)value) : unchecked((ulong)value);
                case TypeCode.Single: return check ? checked((float)value) : unchecked((float)value);
                case TypeCode.Double: return check ? checked((double)value) : unchecked((double)value);
                default: throw new NotSupportedException("Numeric conversion is not supported by the AOT interpreter.");
            }
        }

        private static object CastNumber(ulong value, Type target, bool check)
        {
            switch (Type.GetTypeCode(target))
            {
                case TypeCode.SByte: return check ? checked((sbyte)value) : unchecked((sbyte)value);
                case TypeCode.Byte: return check ? checked((byte)value) : unchecked((byte)value);
                case TypeCode.Int16: return check ? checked((short)value) : unchecked((short)value);
                case TypeCode.UInt16: return check ? checked((ushort)value) : unchecked((ushort)value);
                case TypeCode.Char: return check ? checked((char)value) : unchecked((char)value);
                case TypeCode.Int32: return check ? checked((int)value) : unchecked((int)value);
                case TypeCode.UInt32: return check ? checked((uint)value) : unchecked((uint)value);
                case TypeCode.Int64: return check ? checked((long)value) : unchecked((long)value);
                case TypeCode.UInt64: return check ? checked((ulong)value) : unchecked((ulong)value);
                case TypeCode.Single: return check ? checked((float)value) : unchecked((float)value);
                case TypeCode.Double: return check ? checked((double)value) : unchecked((double)value);
                default: throw new NotSupportedException("Numeric conversion is not supported by the AOT interpreter.");
            }
        }

        private static object CastNumber(double value, Type target, bool check)
        {
            switch (Type.GetTypeCode(target))
            {
                case TypeCode.SByte: return check ? checked((sbyte)value) : unchecked((sbyte)value);
                case TypeCode.Byte: return check ? checked((byte)value) : unchecked((byte)value);
                case TypeCode.Int16: return check ? checked((short)value) : unchecked((short)value);
                case TypeCode.UInt16: return check ? checked((ushort)value) : unchecked((ushort)value);
                case TypeCode.Char: return check ? checked((char)value) : unchecked((char)value);
                case TypeCode.Int32: return check ? checked((int)value) : unchecked((int)value);
                case TypeCode.UInt32: return check ? checked((uint)value) : unchecked((uint)value);
                case TypeCode.Int64: return check ? checked((long)value) : unchecked((long)value);
                case TypeCode.UInt64: return check ? checked((ulong)value) : unchecked((ulong)value);
                case TypeCode.Single: return check ? checked((float)value) : unchecked((float)value);
                case TypeCode.Double: return check ? checked((double)value) : unchecked((double)value);
                default: throw new NotSupportedException("Numeric conversion is not supported by the AOT interpreter.");
            }
        }
    }
}
