using System;
using System.Reflection;

namespace LiteDB
{
    internal static class LinqNumericConversions
    {
        internal static bool IsWidening(Type from, Type to)
        {
            if (from.GetTypeInfo().IsEnum || to.GetTypeInfo().IsEnum) return false;
            if (from == to) return true;

            if (to == typeof(long))
                return from == typeof(int) || from == typeof(uint) || from == typeof(short) ||
                       from == typeof(ushort) || from == typeof(byte) || from == typeof(sbyte);

            if (to == typeof(int))
                return from == typeof(short) || from == typeof(ushort) || from == typeof(byte) || from == typeof(sbyte);

            if (to == typeof(uint)) return from == typeof(byte) || from == typeof(ushort);
            if (to == typeof(short)) return from == typeof(byte) || from == typeof(sbyte);
            if (to == typeof(ushort)) return from == typeof(byte);

            return false;
        }

    }
}
