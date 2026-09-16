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
    internal class ObjectIdResolver : ITypeResolver
    {
        public LinqExpressionBinding ResolveMethod(MethodInfo method)
        {
            switch (method.Name)
            {
                // instance methods
                case "ToString": return c => c.Call("STRING", c.Object());
                case "Equals": return c => c.Binary("=", c.Object(), c.Argument(0));
            };

            return null;
        }

        public LinqExpressionBinding ResolveMember(MemberInfo member)
        {
            switch (member.Name)
            {
                // static properties
                case "Empty": return c => c.Call("OBJECTID", c.Constant("000000000000000000000000"));

                // instance properties
                case "CreationTime": return c => c.Call("OID_CREATIONTIME", c.Object());
            }

            return null;
        }

        public LinqExpressionBinding ResolveCtor(ConstructorInfo ctor)
        {
            var pars = ctor.GetParameters();

            if (pars.Length == 1)
            {
                // string value
                if (pars[0].ParameterType == typeof(string))
                {
                    return c => c.Call("OBJECTID", c.Argument(0));
                }
            }

            return null;
        }
    }
}