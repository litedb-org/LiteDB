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
        public string ResolveMethod(MethodInfo method)
        {
            var qtParams = method.GetParameters().Length;

            switch (method.Name)
            {
                case "Abs": return "ABS(@0)";
                case "Pow": return "POW(@0, @1)";
                case "Round":
                    if (qtParams != 2) throw new ArgumentOutOfRangeException("Method Round need 2 arguments when convert to BsonExpression");
                    return "ROUND(@0, @1)";
            }

            return null;
        }

        public string ResolveMember(MemberInfo member) => null;
        public string ResolveCtor(ConstructorInfo ctor) => null;
    }
}