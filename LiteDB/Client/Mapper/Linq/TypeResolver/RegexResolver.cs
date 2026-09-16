using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using static LiteDB.Constants;

namespace LiteDB
{
    internal class RegexResolver : ITypeResolver
    {
        public LinqExpressionBinding ResolveMethod(MethodInfo method)
        {
            switch (method.Name)
            {
                case "Split": return c => c.Call("SPLIT", c.Argument(0), c.Argument(1), c.Constant(true));
                case "IsMatch": return c => c.Call("IS_MATCH", c.Argument(0), c.Argument(1));
                // missing "Match"
            }

            return null;
        }

        public LinqExpressionBinding ResolveMember(MemberInfo member) => null;
        public LinqExpressionBinding ResolveCtor(ConstructorInfo ctor) => null;
    }
}