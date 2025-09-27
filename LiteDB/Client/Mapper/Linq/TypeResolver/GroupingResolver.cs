using System.Linq;
using System.Reflection;

namespace LiteDB
{
    internal class GroupingResolver : ITypeResolver
    {
        public string ResolveMethod(MethodInfo method) => null;

        public string ResolveMember(MemberInfo member)
        {
            return member.Name == nameof(IGrouping<object, object>.Key) ? "@key" : null;
        }

        public string ResolveCtor(ConstructorInfo ctor) => null;
    }
}
