using System;
using System.Reflection;
using LiteDB.Spatial;
using SpatialMethods = LiteDB.Spatial.Spatial;

namespace LiteDB
{
    internal class SpatialResolver : ITypeResolver
    {
        public string ResolveMethod(MethodInfo method)
        {
            if (method == null)
            {
                return null;
            }

            if (method.DeclaringType == typeof(SpatialExpressions))
            {
                return ResolveSpatialExpressions(method);
            }

            if (method.DeclaringType == typeof(SpatialMethods))
            {
                return ResolveSpatialMethods(method);
            }

            return null;
        }

        public string ResolveMember(MemberInfo member) => null;

        public string ResolveCtor(ConstructorInfo ctor) => null;

        private static string ResolveSpatialExpressions(MethodInfo method)
        {
            switch (method.Name)
            {
                case nameof(SpatialExpressions.Near):
                    return ResolveNearPattern(method);
                case nameof(SpatialExpressions.Within):
                    return "SPATIAL_WITHIN(@0, @1)";
                case nameof(SpatialExpressions.Intersects):
                    return "SPATIAL_INTERSECTS(@0, @1)";
                case nameof(SpatialExpressions.Contains):
                    return "SPATIAL_CONTAINS(@0, @1)";
                case nameof(SpatialExpressions.WithinBoundingBox):
                    return "SPATIAL_WITHIN_BOX(@0, @1, @2, @3, @4)";
            }

            return null;
        }

        private static string ResolveSpatialMethods(MethodInfo method)
        {
            var parameters = method.GetParameters();

            if (method.Name == nameof(SpatialMethods.Near) && parameters.Length == 3 && parameters[0].ParameterType == typeof(GeoPoint))
            {
                return ResolveNearPattern(method);
            }

            if (method.Name == nameof(SpatialMethods.Within) && parameters.Length == 2 && parameters[0].ParameterType == typeof(GeoShape))
            {
                return "SPATIAL_WITHIN(@0, @1)";
            }

            if (method.Name == nameof(SpatialMethods.Intersects) && parameters.Length == 2 && parameters[0].ParameterType == typeof(GeoShape))
            {
                return "SPATIAL_INTERSECTS(@0, @1)";
            }

            if (method.Name == nameof(SpatialMethods.Contains) && parameters.Length == 2 && parameters[0].ParameterType == typeof(GeoShape))
            {
                return "SPATIAL_CONTAINS_POINT(@0, @1)";
            }

            return null;
        }

        private static string ResolveNearPattern(MethodInfo method)
        {
            var parameters = method.GetParameters();

            if (parameters.Length == 3)
            {
                var formula = SpatialMethods.Options.Distance.ToString();
                return $"SPATIAL_NEAR(@0, @1, @2, '{formula}')";
            }

            if (parameters.Length == 4)
            {
                return "SPATIAL_NEAR(@0, @1, @2, @3)";
            }

            throw new NotSupportedException("Unsupported overload for spatial Near expression");
        }
    }
}
