using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace LiteDB
{
    internal partial class Reflection
    {
        private static readonly ConditionalWeakTable<Type, Type[]> _dictionarySchemas = new ConditionalWeakTable<Type, Type[]>();

        internal static void GetDictionaryTypes(Type declaredType, out Type keyType, out Type valueType)
        {
            var arguments = _dictionarySchemas.GetValue(declaredType, GetDictionarySchema);
            keyType = arguments[0];
            valueType = arguments[1];
        }

        internal static bool IsDictionaryInterface(Type type)
        {
            return type.GetTypeInfo().IsInterface && type != typeof(IDictionary) &&
                ((typeof(IDictionary).IsAssignableFrom(type) && type.GetGenericArguments().Length >= 2) || IsGenericDictionaryContract(type) ||
                 type.GetInterfaces().Any(IsGenericDictionaryContract));
        }

        private static Type[] GetDictionarySchema(Type type)
        {
            if (type.GetTypeInfo().IsInterface)
            {
                // Opaque generic interfaces historically selected their own first
                // two arguments, even when an inherited view has a different schema.
                var declared = type.GetGenericArguments();
                if (declared.Length >= 2) return declared.Take(2).ToArray();

                // A declared interface selected the mapping before this fix. Its
                // runtime implementation can expose other, unrelated dictionary views.
                var contracts = type.GetInterfaces().Concat(new[] { type })
                    .Where(IsGenericDictionaryContract).ToArray();
                var contract = contracts.FirstOrDefault(candidate => candidate == type) ?? contracts.FirstOrDefault();
                if (contract != null)
                {
                    var arguments = contract.GetGenericArguments();
                    if (contracts.Any(candidate => !candidate.GetGenericArguments().SequenceEqual(arguments)))
                    {
                        // An opaque combined interface has no single schema. Keep
                        // the old declared-argument convention instead of selecting
                        // whichever inherited view reflection happens to list first.
                        return new[] { typeof(object), typeof(object) };
                    }
                    return arguments;
                }
            }
            else if (typeof(IDictionary).IsAssignableFrom(type))
            {
                // Standard dictionary indexers establish their key/value schema.
                // Custom adapters can expose unrelated generic side stores, so
                // their implementing base's type arguments are not authoritative.
                var map = type.GetInterfaceMap(typeof(IDictionary));
                var setter = typeof(IDictionary).GetProperty("Item").GetSetMethod();
                var implementation = map.TargetMethods[Array.IndexOf(map.InterfaceMethods, setter)].DeclaringType;
                var arguments = implementation.GetGenericArguments();
                if (IsStandardDictionaryImplementation(implementation))
                {
                    return arguments.Take(2).ToArray();
                }

            }

            // Opaque adapters and their non-dictionary base declarations historically
            // supplied the first two declared arguments. Do not infer from side stores.
            var opaqueArguments = type.GetGenericArguments();
            return opaqueArguments.Length >= 2 ? opaqueArguments.Take(2).ToArray() :
                new[] { typeof(object), typeof(object) };
        }

        private static bool IsStandardDictionaryImplementation(Type type)
        {
            if (!type.GetTypeInfo().IsGenericType) return false;
            var definition = type.GetGenericTypeDefinition();
            return definition == typeof(Dictionary<,>) || definition == typeof(SortedDictionary<,>) ||
                definition == typeof(SortedList<,>) || definition == typeof(ConcurrentDictionary<,>) ||
                definition == typeof(ReadOnlyDictionary<,>);
        }

        private static bool IsGenericDictionaryContract(Type type)
        {
            return type.GetTypeInfo().IsGenericType &&
                (type.GetGenericTypeDefinition() == typeof(IDictionary<,>) ||
                 type.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>));
        }
    }
}
