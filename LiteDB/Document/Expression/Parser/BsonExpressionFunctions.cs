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
            foreach (var item in input)
            {
                // execute for each child value and except a first bool value (returns if true)
                var values = mapExpr.Execute(new BsonDocument[] { root }, root, item, collation);

                foreach (var value in values)
                {
                    yield return value;
                }
            }
        }

        public static IEnumerable<BsonValue> FILTER(BsonDocument root, Collation collation, BsonDocument parameters, IEnumerable<BsonValue> input, BsonExpression filterExpr)
        {
            foreach (var item in input)
            {
                // execute for each child value and except a first bool value (returns if true)
                var c = filterExpr.ExecuteScalar(new BsonDocument[] { root }, root, item, collation);

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
                foreach (var item in input)
                {
                    var value = sortExpr.ExecuteScalar(new BsonDocument[] { root }, root, item, collation);

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
