using System.Collections;
using System.Linq;
using System.Reflection;

namespace LiteDB
{
    internal class GroupingResolver : EnumerableResolver
    {
        public override string ResolveMethod(MethodInfo method)
        {
            var name = Reflection.MethodName(method, 1);
            switch (name)
            {
                case "AsEnumerable()": return "*";
                case "Where(Func<T,TResult>)": return "FILTER(* => @1)";
                case "Select(Func<T,TResult>)": return "MAP(* => @1)";
                case "Count()": return "COUNT(*)";
                case "Count(Func<T,TResult>)": return "COUNT(FILTER(* => @1))";
                case "Any()": return "COUNT(*) > 0";
                case "Any(Func<T,TResult>)": return "FILTER(* => @1) ANY %";
                case "All(Func<T,TResult>)": return "FILTER(* => @1) ALL %";
                case "Sum()": return "SUM(*)";
                case "Sum(Func<T,TResult>)": return "SUM(MAP(* => @1))";
                case "Average()": return "AVG(*)";
                case "Average(Func<T,TResult>)": return "AVG(MAP(* => @1))";
                case "Max()": return "MAX(*)";
                case "Max(Func<T,TResult>)": return "MAX(MAP(* => @1))";
                case "Min()": return "MIN(*)";
                case "Min(Func<T,TResult>)": return "MIN(MAP(* => @1))";
            }

            return base.ResolveMethod(method);
        }

        public override string ResolveMember(MemberInfo member)
        {
            if (member.Name == nameof(IGrouping<object, object>.Key))
            {
                return "@key";
            }

            if (member.Name == nameof(ICollection.Count))
            {
                return "COUNT(*)";
            }

            return base.ResolveMember(member);
        }
    }
}
