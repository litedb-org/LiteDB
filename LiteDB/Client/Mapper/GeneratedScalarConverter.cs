using System;

namespace LiteDB
{
    internal static class GeneratedScalarConverter
    {
        internal static bool CanConvert(Type type)
        {
            var target = Nullable.GetUnderlyingType(type) ?? type;

            return target == typeof(BsonValue) ||
                target == typeof(string) ||
                target == typeof(byte[]) ||
                target == typeof(float[]) ||
                target == typeof(ObjectId) ||
                target == typeof(Guid) ||
                target == typeof(DateTime) ||
                target == typeof(DateTimeOffset) ||
                target == typeof(bool) ||
                target == typeof(char) ||
                target == typeof(byte) ||
                target == typeof(sbyte) ||
                target == typeof(short) ||
                target == typeof(ushort) ||
                target == typeof(int) ||
                target == typeof(uint) ||
                target == typeof(long) ||
                target == typeof(ulong) ||
                target == typeof(float) ||
                target == typeof(double) ||
                target == typeof(decimal) ||
                target.IsEnum;
        }

        internal static T Convert<T>(BsonValue value)
        {
            if (value == null || value.IsNull)
            {
                return default;
            }

            var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
            object result;

            if (target == typeof(BsonValue)) result = value;
            else if (target == typeof(string)) result = value.AsString;
            else if (target == typeof(byte[])) result = value.AsBinary;
            else if (target == typeof(ObjectId)) result = value.AsObjectId;
            else if (target == typeof(Guid)) result = value.AsGuid;
            else if (target == typeof(DateTime)) result = value.AsDateTime;
            else if (target == typeof(DateTimeOffset)) result = new DateTimeOffset(value.AsDateTime.ToUniversalTime());
            else if (target == typeof(bool)) result = value.AsBoolean;
            else if (target == typeof(float[])) result = value.AsVector;
            else if (target.IsEnum) result = value.IsString ? Enum.Parse(target, value.AsString) : Enum.ToObject(target, value.RawValue);
            else result = System.Convert.ChangeType(value.RawValue, target);

            return (T)result;
        }
    }
}
