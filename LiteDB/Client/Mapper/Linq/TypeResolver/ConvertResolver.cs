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
    internal class ConvertResolver : ITypeResolver
    {
        public LinqExpressionBinding ResolveMethod(MethodInfo method)
        {
            switch (method.Name)
            {
                case "ToInt32": return c => c.Call("INT32", c.Argument(0));
                case "ToInt64": return c => c.Call("INT64", c.Argument(0));
                case "ToDouble": return c => c.Call("DOUBLE", c.Argument(0));
                case "ToDecimal": return c => c.Call("DECIMAL", c.Argument(0));

                case "ToDateTime": return c => c.Call("DATE", c.Argument(0));
                case "FromBase64String": return c => c.Call("BINARY", c.Argument(0));
                case "ToBoolean": return c => c.Call("BOOL", c.Argument(0));
                case "ToString": return c => c.Call("STRING", c.Argument(0));
            }

            return null;
        }

        public LinqExpressionBinding ResolveMember(MemberInfo member) => null;
        public LinqExpressionBinding ResolveCtor(ConstructorInfo ctor) => null;
    }
}