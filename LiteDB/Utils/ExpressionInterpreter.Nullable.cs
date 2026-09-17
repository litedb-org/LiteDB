using System;

namespace LiteDB
{
    internal static partial class ExpressionInterpreter
    {
        // Boxing Nullable<T> yields either null or a boxed T, never a nullable
        // receiver suitable for ordinary reflection invocation on every runtime.
        private static object NullableMember(string name, Type type, object value, object[] arguments)
        {
            switch (name)
            {
                case "HasValue": case "get_HasValue": return value != null;
                case "Value": case "get_Value": return value ?? throw new InvalidOperationException("Nullable object must have a value.");
                case "GetValueOrDefault": return value ?? (arguments.Length == 1 ? arguments[0] : Activator.CreateInstance(Nullable.GetUnderlyingType(type)));
                case "Equals": return Equals(value, arguments[0]);
                case "GetHashCode": return value?.GetHashCode() ?? 0;
                case "ToString": return value?.ToString() ?? string.Empty;
                default: throw new NotSupportedException("Nullable member " + name + " is not supported by the AOT interpreter.");
            }
        }
    }
}
