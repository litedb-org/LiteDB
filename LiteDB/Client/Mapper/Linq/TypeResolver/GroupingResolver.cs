using System.Collections;
using System.Linq;
using System.Reflection;

namespace LiteDB
{
    internal class GroupingResolver : EnumerableResolver
    {
        public override LinqExpressionBinding ResolveMethod(MethodInfo method)
        {
            var name = Reflection.MethodName(method, 1);
            switch (name)
            {
                case "AsEnumerable()": return c => c.Source();
                case "Where(Func<T,TResult>)": return c => c.Function("FILTER", c.Source(), 1);
                case "Select(Func<T,TResult>)": return c => c.Function("MAP", c.Source(), 1);
                case "Count()": return c => c.Call("COUNT", c.Source());
                case "Count(Func<T,TResult>)": return c => c.Call("COUNT", c.Function("FILTER", c.Source(), 1));
                case "Any()": return c => c.Binary(">", c.Call("COUNT", c.Source()), c.Constant(0));
                case "Any(Func<T,TResult>)": return c => c.Quantifier("ANY", c.Function("FILTER", c.Source(), 1), 1);
                case "All(Func<T,TResult>)": return c => c.Quantifier("ALL", c.Function("FILTER", c.Source(), 1), 1);
                case "Sum()": return c => c.Call("SUM", c.Source());
                case "Sum(Func<T,TResult>)": return c => c.Call("SUM", c.Function("MAP", c.Source(), 1));
                case "Average()": return c => c.Call("AVG", c.Source());
                case "Average(Func<T,TResult>)": return c => c.Call("AVG", c.Function("MAP", c.Source(), 1));
                case "Max()": return c => c.Call("MAX", c.Source());
                case "Max(Func<T,TResult>)": return c => c.Call("MAX", c.Function("MAP", c.Source(), 1));
                case "Min()": return c => c.Call("MIN", c.Source());
                case "Min(Func<T,TResult>)": return c => c.Call("MIN", c.Function("MAP", c.Source(), 1));
            }

            return base.ResolveMethod(method);
        }

        public override LinqExpressionBinding ResolveMember(MemberInfo member)
        {
            if (member.Name == nameof(IGrouping<object, object>.Key))
            {
                return c => c.Parameter("key");
            }

            if (member.Name == nameof(ICollection.Count))
            {
                return c => c.Call("COUNT", c.Source());
            }

            return base.ResolveMember(member);
        }
    }
}
