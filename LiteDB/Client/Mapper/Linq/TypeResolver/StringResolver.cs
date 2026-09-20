using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

using static LiteDB.Constants;

namespace LiteDB
{
    internal class StringResolver : ITypeResolver
    {
        public LinqExpressionBinding ResolveMethod(MethodInfo method)
        {
            var qtParams = method.GetParameters().Length;
            if (qtParams > 0 && method.GetParameters().Last().ParameterType == typeof(StringComparison))
            {
                switch (method.Name)
                {
                    case "StartsWith": return c => c.Call("STRING_STARTSWITH", c.Object(), c.Argument(0), c.Argument(1));
                    case "EndsWith": return c => c.Call("STRING_ENDSWITH", c.Object(), c.Argument(0), c.Argument(1));
                    case "Contains": return c => c.Call("STRING_CONTAINS", c.Object(), c.Argument(0), c.Argument(1));
                    case "IndexOf": return c => c.Call("STRING_INDEXOF",
                        Enumerable.Range(0, qtParams).Select(c.Argument).Prepend(c.Object()).ToArray());
                }
            }

            switch (method.Name)
            {
                case "Count": return c => c.Call("LENGTH", c.Object());
                case "Trim": return c => c.Call("TRIM", c.Object());
                case "TrimStart": return c => c.Call("LTRIM", c.Object());
                case "TrimEnd": return c => c.Call("RTRIM", c.Object());
                case "ToUpper": return c => c.Call("UPPER", c.Object());
                case "ToUpperInvariant": return c => c.Call("UPPER", c.Object());
                case "ToLower": return c => c.Call("LOWER", c.Object());
                case "ToLowerInvariant": return c => c.Call("LOWER", c.Object());
                case "Replace": return c => c.Call("REPLACE", c.Object(), c.Argument(0), c.Argument(1));
                case "PadLeft": return c => c.Call("LPAD", c.Object(), c.Argument(0), c.Argument(1));
                case "PadRight": return c => c.Call("RPAD", c.Object(), c.Argument(0), c.Argument(1));
                case "IndexOf": return qtParams == 1 ? (c => c.Call("INDEXOF", c.Object(), c.Argument(0))) : (c => c.Call("INDEXOF", c.Object(), c.Argument(0), c.Argument(1)));
                case "Substring": return qtParams == 1 ? (c => c.Call("SUBSTRING", c.Object(), c.Argument(0))) : (c => c.Call("SUBSTRING", c.Object(), c.Argument(0), c.Argument(1)));
                case "StartsWith": return c => c.Binary("LIKE", c.Object(), c.Group(c.Binary("+", c.Argument(0), c.Constant("%"))));
                case "Contains": return c => c.Binary("LIKE", c.Object(), c.Group(c.Binary("+", c.Binary("+", c.Constant("%"), c.Argument(0)), c.Constant("%"))));
                case "EndsWith": return c => c.Binary("LIKE", c.Object(), c.Group(c.Binary("+", c.Constant("%"), c.Argument(0))));
                case "ToString": return c => c.Object();
                case "Equals":
                    if (method.GetParameters().Last().ParameterType == typeof(StringComparison))
                        return method.IsStatic ?
                            (c => c.Call("STRING_EQUALS", c.Argument(0), c.Argument(1), c.Argument(2))) :
                            (c => c.Call("STRING_EQUALS_INSTANCE", c.Object(), c.Argument(0), c.Argument(1)));
                    return method.IsStatic ?
                        (c => c.Binary("=", c.Argument(0), c.Argument(1))) :
                        (c => c.Binary("=", c.Object(), c.Argument(0)));

                // static methods
                case "IsNullOrEmpty": return c => c.Group(c.Binary("=", c.Call("LENGTH", c.Argument(0)), c.Constant(0)));
                case "IsNullOrWhiteSpace": return c => c.Group(c.Binary("=", c.Call("LENGTH", c.Call("TRIM", c.Argument(0))), c.Constant(0)));
                case "Format": return null;
                case "Join": return null;
            };

            return null;
        }

        public LinqExpressionBinding ResolveMember(MemberInfo member)
        {
            switch (member.Name)
            {
                case "Length": return c => c.Call("LENGTH", c.Object());
                case "Empty": return c => c.Constant("");
            }

            return null;
        }

        public LinqExpressionBinding ResolveCtor(ConstructorInfo ctor) => null;
    }
}
