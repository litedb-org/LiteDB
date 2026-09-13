using System;
using System.Linq.Expressions;
using System.Text;
using System.Text.RegularExpressions;
using static LiteDB.Constants;

namespace LiteDB
{
    internal static class ExpressionExtensions
    {
        // more dirty as possible: removing ".Select(x => x." sentence
        private static readonly Regex _removeSelect = new Regex(@"\.Select\s*\(\s*\w+\s*=>\s*\w+\.", RegexOptions.Compiled);
        private static readonly Regex _removeList = new Regex(@"\.get_Item\(\d+\)", RegexOptions.Compiled);
        private static readonly Regex _removeArray = new Regex(@"\[\d+\]", RegexOptions.Compiled);

        /// <summary>
        /// Get Path (better ToString) from an Expression.
        /// Support multi levels: x => x.Customer.Address
        /// Support list levels: x => x.Addresses.Select(z => z.StreetName)
        /// </summary>
        public static string GetPath(this Expression expr)
        {
            // quick and dirty solution to support x.Name.SubName
            // http://stackoverflow.com/questions/671968/retrieving-property-name-from-lambda-expression

            // enum properties seem to get compiled with Convert(prop, Int32) wrapper calls on Mono 5.0+
            // this causes path extraction code below to fail, since a clean "x.y.z" string is expected
            // thus we strip out any Converts found, using a loop in case there are nested Convert expressions
            while (expr.NodeType == ExpressionType.Convert || expr.NodeType == ExpressionType.ConvertChecked)
            {
                expr = ((UnaryExpression)expr).Operand;
            }

            // if is a method call, get first
            while(expr.NodeType == ExpressionType.Lambda)
            {
                if (((LambdaExpression)expr).Body is UnaryExpression unary)
                {
                    expr = unary.Operand;
                }
                else
                {
                    break;
                }
            }
            
            var str = expr.ToString(); // gives you: "o => o.Whatever"
            var firstDelim = str.IndexOf('.'); // make sure there is a beginning property indicator; the "." in "o.Whatever" -- this may not be necessary?

            var path = firstDelim < 0 ? str : str.Substring(firstDelim + 1).TrimEnd(')');

            path = _removeList.Replace(path, "");
            path = _removeArray.Replace(path, "");
            path = _removeSelect.Replace(path, ".")
                .Replace(")", "");

            return path;
        }
    }
}
