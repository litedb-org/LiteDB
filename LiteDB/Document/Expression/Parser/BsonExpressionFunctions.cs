using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using static LiteDB.Constants;

namespace LiteDB
{
    internal class BsonExpressionFunctions
    {
        public static IEnumerable<BsonValue> MAP(BsonDocument root, Collation collation, BsonDocument parameters, IEnumerable<BsonValue> input, BsonExpression mapExpr)
        {
            var source = mapExpr.UseSource ? new[] { root } : Array.Empty<BsonDocument>();
            foreach (var item in input)
            {
                if (mapExpr.IsScalar)
                {
                    yield return mapExpr.ExecuteScalar(source, root, item, collation, parameters);
                }
                else
                {
                    foreach (var value in mapExpr.Execute(source, root, item, collation, parameters))
                    {
                        yield return value;
                    }
                }
            }
        }

        public static IEnumerable<BsonValue> FILTER(BsonDocument root, Collation collation, BsonDocument parameters, IEnumerable<BsonValue> input, BsonExpression filterExpr)
        {
            var source = filterExpr.UseSource ? new[] { root } : Array.Empty<BsonDocument>();
            foreach (var item in input)
            {
                // execute for each child value and except a first bool value (returns if true)
                var c = filterExpr.ExecuteScalar(source, root, item, collation, parameters);

                if (c.IsBoolean && c.AsBoolean == true)
                {
                    yield return item;
                }
            }
        }

        public static IEnumerable<BsonValue> SORT(BsonDocument root, Collation collation, BsonDocument parameters, IEnumerable<BsonValue> input, BsonExpression sortExpr, BsonValue order)
        {
            IEnumerable<Tuple<BsonValue, BsonValue>> source()
            {
                var documents = sortExpr.UseSource ? new[] { root } : Array.Empty<BsonDocument>();
                foreach (var item in input)
                {
                    var value = sortExpr.ExecuteScalar(documents, root, item, collation, parameters);

                    yield return new Tuple<BsonValue, BsonValue>(item, value);
                }
            }

            return (order.IsInt32 && order.AsInt32 > 0) || (order.IsString && order.AsString.Equals("asc", StringComparison.OrdinalIgnoreCase)) ?
                source().OrderBy(x => x.Item2, collation).Select(x => x.Item1) :
                source().OrderByDescending(x => x.Item2, collation).Select(x => x.Item1);
        }

        public static IEnumerable<BsonValue> SORT(BsonDocument root, Collation collation, BsonDocument parameters, IEnumerable<BsonValue> input, BsonExpression sortExpr)
        {
            return SORT(root, collation, parameters, input, sortExpr, order: 1);
        }

        public static BsonValue VECTOR_SIM(BsonDocument root, Collation collation, BsonDocument parameters, BsonValue left, BsonValue right)
        {
            return BsonExpressionMethods.VECTOR_SIM(left, right);
        }
    }
}
