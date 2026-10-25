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
    internal class DateTimeResolver : ITypeResolver
    {
        public LinqExpressionBinding ResolveMethod(MethodInfo method)
        {
            switch (method.Name)
            {
                // instance methods
                case "AddYears": return c => c.Call("DATEADD", c.Constant("y"), c.Argument(0), c.Object());
                case "AddMonths": return c => c.Call("DATEADD", c.Constant("M"), c.Argument(0), c.Object());
                case "AddDays": return c => c.Call("DATEADD", c.Constant("d"), c.Argument(0), c.Object());
                case "AddHours": return c => c.Call("DATEADD", c.Constant("h"), c.Argument(0), c.Object());
                case "AddMinutes": return c => c.Call("DATEADD", c.Constant("m"), c.Argument(0), c.Object());
                case "AddSeconds": return c => c.Call("DATEADD", c.Constant("s"), c.Argument(0), c.Object());
                case "ToString":
                    var pars = method.GetParameters();
                    if (pars.Length == 0) return c => c.Call("STRING", c.Object());
                    else if (pars.Length == 1 && pars[0].ParameterType == typeof(string)) return c => c.Call("FORMAT", c.Object(), c.Argument(0));
                    break;

                case "ToUniversalTime": return c => c.Call("TO_UTC", c.Object());
                case "ToLocalTime": return c => c.Call("TO_LOCAL", c.Object());
                // static methods
                case "Parse": return c => c.Call("DATETIME", c.Argument(0));
                case "Equals": return c => c.Binary("=", c.Object(), c.Argument(0));
            };

            return null;
        }

        public LinqExpressionBinding ResolveMember(MemberInfo member)
        {
            switch (member.Name)
            {
                // static properties
                case "Now": return c => c.Call("NOW");
                case "UtcNow": return c => c.Call("NOW_UTC");
                case "Today": return c => c.Call("TODAY");

                // instance properties
                case "Year": return c => c.Call("YEAR", c.Object());
                case "Month": return c => c.Call("MONTH", c.Object());
                case "Day": return c => c.Call("DAY", c.Object());
                case "Hour": return c => c.Call("HOUR", c.Object());
                case "Minute": return c => c.Call("MINUTE", c.Object());
                case "Second": return c => c.Call("SECOND", c.Object());
                case "Date": return c => c.Call("DATETIME", c.Call("YEAR", c.Object()), c.Call("MONTH", c.Object()), c.Call("DAY", c.Object()));
            }

            return null;
        }

        public LinqExpressionBinding ResolveCtor(ConstructorInfo ctor)
        {
            var pars = ctor.GetParameters();

            if (pars.Length == 3)
            {
                // int year, int month, int day
                if (pars[0].ParameterType == typeof(int) && pars[1].ParameterType == typeof(int) && pars[2].ParameterType == typeof(int))
                {
                    return c => c.Call("DATETIME", c.Argument(0), c.Argument(1), c.Argument(2));
                }
            }

            return null;
        }
    }
}
