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
        public virtual string ResolveMethod(MethodInfo method)
        {
            // all methods in Enumerable are Extensions (static methods), so first parameter is IEnumerable
            var name = Reflection.MethodName(method, 1); 

            switch (name)
            {
                // get all items
                case "AsEnumerable()": return "@0[*]";

                // get fixed index item
                case "get_Item(int)": return "#[@0]";
                case "ElementAt(int)": return "@0[@1]";
                case "Single()":
                case "First()":
                case "SingleOrDefault()":
                case "FirstOrDefault()": return "@0[0]";
                case "Last()":
                case "LastOrDefault()": return "@0[-1]";

                // get single item but with predicate function
                case "Single(Func<T,TResult>)":
                case "First(Func<T,TResult>)":
                case "SingleOrDefault(Func<T,TResult>)":
                case "FirstOrDefault(Func<T,TResult>)": return "FIRST(FILTER(@0 => @1))";
                case "Last(Func<T,TResult>)":
                case "LastOrDefault(Func<T,TResult>)": return "LAST(FILTER(@0 => @1))";

                // filter
                case "Where(Func<T,TResult>)": return "FILTER(@0 => @1)";
                
                // map
                case "Select(Func<T,TResult>)": return "MAP(@0 => @1)";

                // aggregate
                case "Count()": return "COUNT(@0)";
                case "Sum()": return "SUM(@0)";
                case "Average()": return "AVG(@0)";
                case "Max()": return "MAX(@0)";
                case "Min()": return "MIN(@0)";

                // aggregate
                case "Count(Func<T,TResult>)": return "COUNT(FILTER(@0 => @1))";
                case "Sum(Func<T,TResult>)": return "SUM(MAP(@0 => @1))";
                case "Average(Func<T,TResult>)": return "AVG(MAP(@0 => @1))";
                case "Max(Func<T,TResult>)": return "MAX(MAP(@0 => @1))";
                case "Min(Func<T,TResult>)": return "MIN(MAP(@0 => @1))";

                // convert to array
                case "ToList()": 
                case "ToArray()": return "ARRAY(@0)";

                // any/all special cases
                case "Any(Func<T,TResult>)": return "@0 ANY %";
                case "All(Func<T,TResult>)": return "@0 ALL %";
                case "Any()": return "COUNT(@0) > 0";
            }

            // special Contains method
            switch(method.Name)
            {
                case "Contains": return "@0 ANY = @1";
            };

            return null;
        }

        public virtual string ResolveMember(MemberInfo member)
        {
            // this both members are not from IEnumerable:
            // but any IEnumerable type will run this resolver (IList, ICollection)
            switch(member.Name)
            {
                case "Length": return "LENGTH(#)";
                case "Count": return "COUNT(#)";
            }

            return null;
        }

        public string ResolveCtor(ConstructorInfo ctor) => null;
    }
}