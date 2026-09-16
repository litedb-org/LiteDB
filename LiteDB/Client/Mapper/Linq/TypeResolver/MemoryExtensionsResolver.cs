using System.Reflection;

namespace LiteDB
{
    internal class MemoryExtensionsResolver : ITypeResolver
    {
        public LinqExpressionBinding ResolveMethod(MethodInfo method)
        {
            if (method.Name != nameof(System.MemoryExtensions.Contains))
                return null;
            var parameters = method.GetParameters();

            if (parameters.Length == 2)
            {
                return c => c.Binary("ANY =", c.Argument(0), c.Argument(1));
            }

            // Support the 3-parameter overload only when comparer defaults to null.
            if (parameters.Length == 3)
            {
                var third = parameters[2];

                if (third.HasDefaultValue && third.DefaultValue == null)
                {
                    return c => c.Binary("ANY =", c.Argument(0), c.Argument(1));
                }
            }

            return null;
        }

        public LinqExpressionBinding ResolveMember(MemberInfo member) => null;

        public LinqExpressionBinding ResolveCtor(ConstructorInfo ctor) => null;
    }
}
