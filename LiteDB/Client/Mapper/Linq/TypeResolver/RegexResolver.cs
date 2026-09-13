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
        public string ResolveMethod(MethodInfo method)
        {
            switch (method.Name)
            {
                case "Split": return "SPLIT(@0, @1, true)";
                case "IsMatch": return "IS_MATCH(@0, @1)";
                // missing "Match"
            }

            return null;
        }

        public string ResolveMember(MemberInfo member) => null;
        public string ResolveCtor(ConstructorInfo ctor) => null;
    }
}