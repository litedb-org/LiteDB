using System.Reflection;

namespace LiteDB
{
    internal class MemoryExtensionsResolver : ITypeResolver
    {
        public string ResolveMethod(MethodInfo method)
        {
            if (method.Name == nameof(System.MemoryExtensions.Contains))
            {
                var parameters = method.GetParameters();

                if (parameters.Length == 2)
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
