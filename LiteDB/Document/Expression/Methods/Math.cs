using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using static LiteDB.Constants;

namespace LiteDB
{
    internal partial class BsonExpressionMethods
    {
        /// <summary>
        /// Apply absolute value (ABS) method in all number values
        /// </summary>
        public static BsonValue ABS(BsonValue value)
        {
            switch (value.Type)
            {
                case BsonType.Int32: return Math.Abs(value.AsInt32); 
                case BsonType.Int64: return Math.Abs(value.AsInt64); 
                case BsonType.Double: return Math.Abs(value.AsDouble); 
                case BsonType.Decimal: return Math.Abs(value.AsDecimal); 
            }

            return BsonValue.Null;
        }

        /// <summary>
        /// Round number method in all number values
        /// </summary>
        public static BsonValue ROUND(BsonValue value, BsonValue digits)
        {
            if (digits.IsNumber)
            {
                switch (value.Type)
                {
                    case BsonType.Int32: return value.AsInt32;
                    case BsonType.Int64: return value.AsInt64;
                    case BsonType.Double: return Math.Round(value.AsDouble, digits.AsInt32);
                    case BsonType.Decimal: return Math.Round(value.AsDecimal, digits.AsInt32);
                }

            }

            return BsonValue.Null;
        }

        /// <summary>
        /// Implement POWER (x and y)
        /// </summary>
        public static BsonValue POW(BsonValue x, BsonValue y)
        {
            if (x.IsNumber && y.IsNumber)
            {
                return Math.Pow(x.AsDouble, y.AsDouble);
            }

            return BsonValue.Null;
        }
    }
}
