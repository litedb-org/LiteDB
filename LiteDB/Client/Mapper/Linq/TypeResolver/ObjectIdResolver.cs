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
        public string ResolveMethod(MethodInfo method)
        {
            switch (method.Name)
            {
                // instance methods
                case "ToString": return "STRING(#)";
                case "Equals": return "# = @0";
            };

            return null;
        }

        public string ResolveMember(MemberInfo member)
        {
            switch (member.Name)
            {
                // static properties
                case "Empty": return "OBJECTID('000000000000000000000000')";

                // instance properties
                case "CreationTime": return "OID_CREATIONTIME(#)";
            }

            return null;
        }

        public string ResolveCtor(ConstructorInfo ctor)
        {
            var pars = ctor.GetParameters();

            if (pars.Length == 1)
            {
                // string value
                if (pars[0].ParameterType == typeof(string))
                {
                    return "OBJECTID(@0)";
                }
            }

            return null;
        }
    }
}