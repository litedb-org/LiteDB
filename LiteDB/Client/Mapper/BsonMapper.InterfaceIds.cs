using System;
using System.Linq;
using System.Reflection;

namespace LiteDB
{
    public partial class BsonMapper
    {
        internal string ResolveAbstractIdField(EntityMapper declared, MemberMapper member)
        {
            if ((!declared.ForType.IsInterface && !declared.ForType.IsAbstract) ||
                declared.UsesCustomIdSelection || member.HasExplicitFieldName || declared.Id?.HasExplicitFieldName == true)
                return member.FieldName;

            var implementations = _entities.Values.Where(entity =>
                entity.IsInitialized && !entity.ForType.IsInterface && !entity.ForType.IsAbstract &&
                declared.ForType.IsAssignableFrom(entity.ForType)).ToArray();
            if (implementations.Length == 0) return member.FieldName;

            var groups = implementations.Select(entity => GetDeclaredMatches(entity, member)).ToArray();
            var matches = groups.SelectMany(group => group).ToArray();
            if (member.FieldName != "_id" && !matches.Any(candidate => candidate?.FieldName == "_id")) return member.FieldName;
            if (groups.All(group => group.Length > 0) && matches.All(candidate => candidate.FieldName == matches[0].FieldName))
                return matches[0].FieldName;

            throw new NotSupportedException("Known implementations disagree on the _id mapping for " +
                declared.ForType.FullName + "." + member.MemberName + ". Configure the declared type's ID mapping explicitly.");
        }

        private static MemberMapper[] GetDeclaredMatches(EntityMapper entity, MemberMapper declared)
        {
            var matches = entity.Members.Where(candidate => MatchesDeclaredMember(entity.ForType, candidate, declared)).ToArray();
            if (declared.ReflectedMember?.DeclaringType.IsInterface == true) return matches;
            // Reflection can expose both the inherited and covariant override
            // properties. The most-derived accessor owns that class slot.
            return matches.Where(candidate => !matches.Any(other =>
                candidate.ReflectedMember.DeclaringType != other.ReflectedMember.DeclaringType &&
                candidate.ReflectedMember.DeclaringType.IsAssignableFrom(other.ReflectedMember.DeclaringType))).ToArray();
        }

        private static bool MatchesDeclaredMember(Type implementation, MemberMapper candidate, MemberMapper declared)
        {
            if (!declared.DataType.IsAssignableFrom(candidate.DataType)) return false;
            if (declared.ReflectedMember is PropertyInfo property && property.DeclaringType.IsInterface &&
                candidate.ReflectedMember is PropertyInfo concrete)
            {
                var accessor = property.GetMethod ?? property.SetMethod;
                var contracts = implementation.GetInterfaces();
                var applicable = contracts.Contains(property.DeclaringType)
                    ? new[] { property.DeclaringType }
                    : contracts.Where(property.DeclaringType.IsAssignableFrom);
                foreach (var contract in applicable)
                {
                    var mapping = implementation.GetInterfaceMap(contract);
                    var index = Array.FindIndex(mapping.InterfaceMethods, method =>
                        method.Module == accessor.Module && method.MetadataToken == accessor.MetadataToken);
                    if (index >= 0 && (mapping.TargetMethods[index] == concrete.GetMethod || mapping.TargetMethods[index] == concrete.SetMethod))
                        return true;
                }
                return false;
            }
            if (declared.ReflectedMember is PropertyInfo baseProperty && candidate.ReflectedMember is PropertyInfo implementationProperty)
            {
                var accessor = baseProperty.GetMethod ?? baseProperty.SetMethod;
                var target = implementationProperty.GetMethod ?? implementationProperty.SetMethod;
                return GetVirtualDefinition(accessor) == GetVirtualDefinition(target);
            }
            return candidate.ReflectedMember == declared.ReflectedMember;
        }

        private static bool HasCustomIdSelection(Type type)
        {
            for (; type != typeof(BsonMapper); type = type.BaseType)
            {
                if (type.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Any(method =>
                    method.Name == nameof(GetIdMember) && method.IsVirtual && !method.IsGenericMethod &&
                    method.GetParameters().Length == 1 &&
                    method.GetParameters()[0].ParameterType == typeof(System.Collections.Generic.IEnumerable<MemberInfo>) &&
                    GetVirtualDefinition(method).DeclaringType == typeof(BsonMapper))) return true;
            }
            return false;
        }

        private static MethodInfo GetVirtualDefinition(MethodInfo accessor)
        {
            // Covariant returns use a new CLR slot with an explicit override marker.
            if (accessor.GetCustomAttributesData().Any(attribute =>
                attribute.AttributeType.FullName == "System.Runtime.CompilerServices.PreserveBaseOverridesAttribute"))
            {
                for (var parent = accessor.DeclaringType.BaseType; parent != null; parent = parent.BaseType)
                {
                    var inherited = parent.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                        .FirstOrDefault(method => method.IsVirtual && method.Name == accessor.Name &&
                            method.GetParameters().Select(parameter => parameter.ParameterType)
                                .SequenceEqual(accessor.GetParameters().Select(parameter => parameter.ParameterType)) &&
                            method.ReturnType.IsAssignableFrom(accessor.ReturnType));
                    if (inherited != null) return GetVirtualDefinition(inherited);
                }
            }
            return accessor.GetBaseDefinition();
        }
    }
}
