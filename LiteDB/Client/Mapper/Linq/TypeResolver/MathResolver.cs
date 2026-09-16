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
    internal class MathResolver : ITypeResolver
    {
        public LinqExpressionBinding ResolveMethod(MethodInfo method)
        {
            var qtParams = method.GetParameters().Length;

            switch (method.Name)
            {
                case "Abs": return c => c.Call("ABS", c.Argument(0));
                case "Pow": return c => c.Call("POW", c.Argument(0), c.Argument(1));
                case "Round":
                    if (qtParams != 2) throw new ArgumentOutOfRangeException("Method Round need 2 arguments when convert to BsonExpression");
                    return c => c.Call("ROUND", c.Argument(0), c.Argument(1));
            }

            return null;
        }

        public LinqExpressionBinding ResolveMember(MemberInfo member) => null;
        public LinqExpressionBinding ResolveCtor(ConstructorInfo ctor) => null;
    }
}