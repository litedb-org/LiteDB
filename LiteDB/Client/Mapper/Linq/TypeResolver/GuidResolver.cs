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
    internal class GuidResolver : ITypeResolver
    {
        public LinqExpressionBinding ResolveMethod(MethodInfo method)
        {
            switch (method.Name)
            {
                // instance methods
                case "ToString": return c => c.Call("STRING", c.Object());

                // static methods
                case "NewGuid": return c => c.Call("GUID");
                case "Parse": return c => c.Call("GUID", c.Argument(0));
                case "TryParse": throw new NotSupportedException("There is no TryParse translate. Use Guid.Parse()");
                case "Equals": return c => c.Binary("=", c.Object(), c.Argument(0));
            }

            return null;
        }

        public LinqExpressionBinding ResolveMember(MemberInfo member)
        {
            switch (member.Name)
            {
                // static properties
                case "Empty": return c => c.Call("GUID", c.Constant("00000000-0000-0000-0000-000000000000"));
            }

            return null;
        }

        public LinqExpressionBinding ResolveCtor(ConstructorInfo ctor)
        {
            var pars = ctor.GetParameters();

            if (pars.Length == 1)
            {
                // string s
                if (pars[0].ParameterType == typeof(string))
                {
                    return c => c.Call("GUID", c.Argument(0));
                }
            }

            return null;
        }
    }
}