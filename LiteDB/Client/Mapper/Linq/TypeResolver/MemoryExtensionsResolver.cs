using System.Reflection;

namespace LiteDB
{
    internal class MemoryExtensionsResolver : ITypeResolver
    {
        public string ResolveMethod(MethodInfo method)
        {
            if (method.Name != nameof(System.MemoryExtensions.Contains))
                return null;
            var parameters = method.GetParameters();

            if (parameters.Length == 2)
            {
                return "@0 ANY = @1";
            }

            // Support the 3-parameter overload only when comparer defaults to null.
            if (parameters.Length == 3)
            {
                var third = parameters[2];

                if (third.HasDefaultValue && third.DefaultValue == null)
                {
                    return "@0 ANY = @1";
                }
            }

            return null;
        }

        public string ResolveMember(MemberInfo member) => null;

        public string ResolveCtor(ConstructorInfo ctor) => null;
    }
}
