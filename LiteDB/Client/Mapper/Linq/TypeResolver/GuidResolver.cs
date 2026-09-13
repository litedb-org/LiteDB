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
        public string ResolveMethod(MethodInfo method)
        {
            switch (method.Name)
            {
                // instance methods
                case "ToString": return "STRING(#)";

                // static methods
                case "NewGuid": return "GUID()";
                case "Parse": return "GUID(@0)";
                case "TryParse": throw new NotSupportedException("There is no TryParse translate. Use Guid.Parse()");
                case "Equals": return "# = @0";
            }

            return null;
        }

        public string ResolveMember(MemberInfo member)
        {
            switch (member.Name)
            {
                // static properties
                case "Empty": return "GUID('00000000-0000-0000-0000-000000000000')";
            }

            return null;
        }

        public string ResolveCtor(ConstructorInfo ctor)
        {
            var pars = ctor.GetParameters();

            if (pars.Length == 1)
            {
                // string s
                if (pars[0].ParameterType == typeof(string))
                {
                    return "GUID(@0)";
                }
            }

            return null;
        }
    }
}