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
    internal class NumberResolver : ITypeResolver
    {
        private readonly string _parseMethod;

        public NumberResolver(string parseMethod)
        {
            _parseMethod = parseMethod;
        }

        public LinqExpressionBinding ResolveMethod(MethodInfo method)
        {
            switch (method.Name)
            {
                // instance methods
                case "ToString":
                    var pars = method.GetParameters();
                    if (pars.Length == 0) return c => c.Call("STRING", c.Object());
                    else if (pars.Length == 1 && pars[0].ParameterType == typeof(string)) return c => c.Call("FORMAT", c.Object(), c.Argument(0));
                    break;

                // static methods
                case "Parse": return c => c.Call(_parseMethod, c.Argument(0));
                case "Equals": return c => c.Binary("=", c.Object(), c.Argument(0));
            };

            return null;
        }

        public LinqExpressionBinding ResolveMember(MemberInfo member) => null;
        public LinqExpressionBinding ResolveCtor(ConstructorInfo ctor) => null;
    }
}