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
    internal interface ITypeResolver
    {
        LinqExpressionBinding ResolveMethod(MethodInfo method);

        LinqExpressionBinding ResolveMember(MemberInfo member);

        LinqExpressionBinding ResolveCtor(ConstructorInfo ctor);
    }
}