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
    internal class NullableResolver : ITypeResolver
    {
        public LinqExpressionBinding ResolveMethod(MethodInfo method)
        {
            return null;
        }

        public LinqExpressionBinding ResolveMember(MemberInfo member)
        {
            switch (member.Name)
            {
                case "HasValue": return c => c.Group(c.Binary("=", c.Call("IS_NULL", c.Object()), c.Constant(false)));
                case "Value": return c => c.Object();
            }

            return null;
        }

        public LinqExpressionBinding ResolveCtor(ConstructorInfo ctor) => null;
    }
}
