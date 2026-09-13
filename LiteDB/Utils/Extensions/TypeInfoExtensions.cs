using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Reflection;
using System.Collections;
using System.Runtime.CompilerServices;

namespace LiteDB
{
    internal static class TypeInfoExtensions
    {
#if HAVE_TYPE_INFO
        public static Type[] GetGenericArguments(this TypeInfo type)
        {
            return type.GenericTypeArguments;
        }
#else
        // In 4.5+, TypeInfo has most of the reflection methods previously on type
        // This allows code to be shared between 3.5 && 4.5+ projects
        public static Type GetTypeInfo(this Type type)
        {
            return type;
        }
#endif

        public static bool IsAnonymousType(this Type type)
        {
            bool isAnonymousType =
                type.FullName.Contains("AnonymousType") &&
                type.GetTypeInfo().GetCustomAttributes(typeof(CompilerGeneratedAttribute), false).Any();

            return isAnonymousType;
        }

        public static bool IsEnumerable(this Type type)
        {
            return
                type != typeof(String) &&
                typeof(IEnumerable).IsAssignableFrom(type);
        }
    }
}
