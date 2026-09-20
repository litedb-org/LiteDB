using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using static LiteDB.Constants;

namespace LiteDB
{
    internal class EnumerableResolver : ITypeResolver
    {
        public virtual LinqExpressionBinding ResolveMethod(MethodInfo method)
        {
            if (DictionaryResolver.IsContainsKey(method))
                return c => c.Call("CONTAINSKEY", c.Object(), c.Argument(0));

            // all methods in Enumerable are Extensions (static methods), so first parameter is IEnumerable
            var name = Reflection.MethodName(method, 1); 

            switch (name)
            {
                // get all items
                case "AsEnumerable()": return c => c.Items(c.Argument(0));

                // get fixed index item
                case "get_Item(int)": return c => c.Index(c.Object(), c.Argument(0));
                case "ElementAt(int)": return c => c.Index(c.Argument(0), c.Argument(1));
                case "Single()":
                case "First()":
                case "SingleOrDefault()":
                case "FirstOrDefault()": return c => c.Index(c.Argument(0), 0);
                case "Last()":
                case "LastOrDefault()": return c => c.Index(c.Argument(0), -1);

                // get single item but with predicate function
                case "Single(Func<T,TResult>)":
                case "First(Func<T,TResult>)":
                case "SingleOrDefault(Func<T,TResult>)":
                case "FirstOrDefault(Func<T,TResult>)": return c => c.Call("FIRST", c.Function("FILTER", c.Argument(0), 1));
                case "Last(Func<T,TResult>)":
                case "LastOrDefault(Func<T,TResult>)": return c => c.Call("LAST", c.Function("FILTER", c.Argument(0), 1));

                // filter
                case "Where(Func<T,TResult>)": return c => c.Function("FILTER", c.Argument(0), 1);
                
                // map
                case "Select(Func<T,TResult>)": return c => c.Function("MAP", c.Argument(0), 1);

                // aggregate
                case "Count()": return c => c.Call("COUNT", c.Argument(0));
                case "Sum()": return c => c.Call("SUM", c.Argument(0));
                case "Average()": return c => c.Call("AVG", c.Argument(0));
                case "Max()": return c => c.Call("MAX", c.Argument(0));
                case "Min()": return c => c.Call("MIN", c.Argument(0));

                // aggregate
                case "Count(Func<T,TResult>)": return c => c.Call("COUNT", c.Function("FILTER", c.Argument(0), 1));
                case "Sum(Func<T,TResult>)": return c => c.Call("SUM", c.Function("MAP", c.Argument(0), 1));
                case "Average(Func<T,TResult>)": return c => c.Call("AVG", c.Function("MAP", c.Argument(0), 1));
                case "Max(Func<T,TResult>)": return c => c.Call("MAX", c.Function("MAP", c.Argument(0), 1));
                case "Min(Func<T,TResult>)": return c => c.Call("MIN", c.Function("MAP", c.Argument(0), 1));

                // convert to array
                case "ToList()": 
                case "ToArray()": return c => c.Call("ARRAY", c.Argument(0));

                // any/all special cases
                case "Any(Func<T,TResult>)": return c => c.Quantifier("ANY", c.Argument(0), 1);
                case "All(Func<T,TResult>)": return c => c.Quantifier("ALL", c.Argument(0), 1);
                case "Any()": return c => c.Binary(">", c.Call("COUNT", c.Argument(0)), c.Constant(0));
            }

            // special Contains method
            switch(method.Name)
            {
                case "Contains": return c => c.Binary("ANY =", c.Argument(0), c.Argument(1));
            };

            return null;
        }

        public virtual LinqExpressionBinding ResolveMember(MemberInfo member)
        {
            // this both members are not from IEnumerable:
            // but any IEnumerable type will run this resolver (IList, ICollection)
            switch(member.Name)
            {
                case "Length": return c => c.Call("LENGTH", c.Object());
                case "Count": return c => c.Call("COUNT", c.Object());
            }

            return null;
        }

        public LinqExpressionBinding ResolveCtor(ConstructorInfo ctor) => null;
    }
}
