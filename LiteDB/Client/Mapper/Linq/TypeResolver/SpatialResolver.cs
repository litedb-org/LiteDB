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

            if (method.DeclaringType != typeof(SpatialMethods))
            {
                return null;
            }

            var parameters = method.GetParameters();

            if (method.Name == nameof(SpatialMethods.Near) && parameters.Length == 3 && parameters[0].ParameterType == typeof(GeoPoint))
            {
                return "SPATIAL_NEAR(@0, @1, @2)";
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

        public string ResolveMember(MemberInfo member) => null;

        public string ResolveCtor(ConstructorInfo ctor) => null;
    }
}
