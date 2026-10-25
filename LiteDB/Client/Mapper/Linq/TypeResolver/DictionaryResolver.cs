using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace LiteDB
{
    internal static class DictionaryResolver
    {
        public static bool IsContainsKey(MethodInfo method)
        {
            var parameters = method.GetParameters();
            if (method.IsStatic || method.Name != "ContainsKey" || method.ReturnType != typeof(bool) ||
                parameters.Length != 1 || parameters[0].ParameterType != typeof(string))
            {
                return false;
            }

            var declaringType = method.DeclaringType;
            var contracts = declaringType.GetInterfaces().Concat(new[] { declaringType })
                .Where(type => type.IsGenericType && type.GetGenericArguments()[0] == typeof(string) &&
                    (type.GetGenericTypeDefinition() == typeof(IDictionary<,>) ||
                     type.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));

            foreach (var contract in contracts)
            {
                if (declaringType.IsInterface) return contract.GetMethods().Contains(method);

                var map = declaringType.GetInterfaceMap(contract);
                for (var i = 0; i < map.InterfaceMethods.Length; i++)
                {
                    if (map.InterfaceMethods[i].Name == "ContainsKey" && map.TargetMethods[i] == method)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
