using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace LiteDB
{
    // The semantic construction boundary shared by the text and LINQ frontends.
    internal static partial class BsonExpressionFactory
    {
        private static MethodInfo M(string s) => typeof(BsonExpressionOperators).GetMethod(s);

        /// <summary>
        /// Operation definition by methods with defined expression type (operators are in precedence order)
        /// </summary>
        internal static readonly Dictionary<string, Tuple<string, MethodInfo, BsonExpressionType>> Operators = new Dictionary<string, Tuple<string, MethodInfo, BsonExpressionType>>
        {
            // arithmetic
            ["%"] = Tuple.Create("%", M("MOD"), BsonExpressionType.Modulo),
            ["/"] = Tuple.Create("/", M("DIVIDE"), BsonExpressionType.Divide),
            ["*"] = Tuple.Create("*", M("MULTIPLY"), BsonExpressionType.Multiply),
            ["+"] = Tuple.Create("+", M("ADD"), BsonExpressionType.Add),
            ["-"] = Tuple.Create("-", M("MINUS"), BsonExpressionType.Subtract),

            // vector similarity operator returns the cosine distance between two vectors
            ["VECTOR_SIM"] = Tuple.Create(" VECTOR_SIM ", M("VECTOR_SIM"), BsonExpressionType.VectorSim),

            // predicate
            ["LIKE"] = Tuple.Create(" LIKE ", M("LIKE"), BsonExpressionType.Like),
            ["BETWEEN"] = Tuple.Create(" BETWEEN ", M("BETWEEN"), BsonExpressionType.Between),
            ["IN"] = Tuple.Create(" IN ", M("IN"), BsonExpressionType.In),

            [">"] = Tuple.Create(">", M("GT"), BsonExpressionType.GreaterThan),
            [">="] = Tuple.Create(">=", M("GTE"), BsonExpressionType.GreaterThanOrEqual),
            ["<"] = Tuple.Create("<", M("LT"), BsonExpressionType.LessThan),
            ["<="] = Tuple.Create("<=", M("LTE"), BsonExpressionType.LessThanOrEqual),

            ["!="] = Tuple.Create("!=", M("NEQ"), BsonExpressionType.NotEqual),
            ["="] = Tuple.Create("=", M("EQ"), BsonExpressionType.Equal),

            ["ANY LIKE"] = Tuple.Create(" ANY LIKE ", M("LIKE_ANY"), BsonExpressionType.Like),
            ["ANY BETWEEN"] = Tuple.Create(" ANY BETWEEN ", M("BETWEEN_ANY"), BsonExpressionType.Between),
            ["ANY IN"] = Tuple.Create(" ANY IN ", M("IN_ANY"), BsonExpressionType.In),

            ["ANY >"] = Tuple.Create(" ANY>", M("GT_ANY"), BsonExpressionType.GreaterThan),
            ["ANY >="] = Tuple.Create(" ANY>=", M("GTE_ANY"), BsonExpressionType.GreaterThanOrEqual),
            ["ANY <"] = Tuple.Create(" ANY<", M("LT_ANY"), BsonExpressionType.LessThan),
            ["ANY <="] = Tuple.Create(" ANY<=", M("LTE_ANY"), BsonExpressionType.LessThanOrEqual),

            ["ANY !="] = Tuple.Create(" ANY!=", M("NEQ_ANY"), BsonExpressionType.NotEqual),
            ["ANY ="] = Tuple.Create(" ANY=", M("EQ_ANY"), BsonExpressionType.Equal),

            ["ALL LIKE"] = Tuple.Create(" ALL LIKE ", M("LIKE_ALL"), BsonExpressionType.Like),
            ["ALL BETWEEN"] = Tuple.Create(" ALL BETWEEN ", M("BETWEEN_ALL"), BsonExpressionType.Between),
            ["ALL IN"] = Tuple.Create(" ALL IN ", M("IN_ALL"), BsonExpressionType.In),

            ["ALL >"] = Tuple.Create(" ALL>", M("GT_ALL"), BsonExpressionType.GreaterThan),
            ["ALL >="] = Tuple.Create(" ALL>=", M("GTE_ALL"), BsonExpressionType.GreaterThanOrEqual),
            ["ALL <"] = Tuple.Create(" ALL<", M("LT_ALL"), BsonExpressionType.LessThan),
            ["ALL <="] = Tuple.Create(" ALL<=", M("LTE_ALL"), BsonExpressionType.LessThanOrEqual),

            ["ALL !="] = Tuple.Create(" ALL!=", M("NEQ_ALL"), BsonExpressionType.NotEqual),
            ["ALL ="] = Tuple.Create(" ALL=", M("EQ_ALL"), BsonExpressionType.Equal),

            // logic (will use Expression.AndAlso|OrElse)
            ["AND"] = Tuple.Create(" AND ", (MethodInfo)null, BsonExpressionType.And),
            ["OR"] = Tuple.Create(" OR ", (MethodInfo)null, BsonExpressionType.Or),
        };

        internal static readonly MethodInfo _parameterPathMethod = M("PARAMETER_PATH");
        internal static readonly MethodInfo _memberPathMethod = M("MEMBER_PATH");
        internal static readonly MethodInfo _arrayIndexMethod = M("ARRAY_INDEX");
        internal static readonly MethodInfo _arrayFilterMethod = M("ARRAY_FILTER");

        internal static readonly MethodInfo _documentInitMethod = M("DOCUMENT_INIT");
        internal static readonly MethodInfo _arrayInitMethod = M("ARRAY_INIT");

        internal static readonly MethodInfo _itemsMethod = typeof(BsonExpressionMethods).GetMethod("ITEMS");
        internal static readonly MethodInfo _arrayMethod = typeof(BsonExpressionMethods).GetMethod("ARRAY");

    }
}
