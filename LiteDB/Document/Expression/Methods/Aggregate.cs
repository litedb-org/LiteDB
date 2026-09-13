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
        /// Count all values. Return a single value
        /// </summary>
        public static BsonValue COUNT(IEnumerable<BsonValue> values)
        {
            return values.Count();
        }

        /// <summary>
        /// Find minimal value from all values (number values only). Return a single value
        /// </summary>
        public static BsonValue MIN(IEnumerable<BsonValue> values)
        {
            var min = BsonValue.MaxValue;

            foreach(var value in values)
            {
                if (value.CompareTo(min) <= 0)
                {
                    min = value;
                }
            }

            return min == BsonValue.MaxValue ? BsonValue.MinValue : min;
        }

        /// <summary>
        /// Find max value from all values (number values only). Return a single value
        /// </summary>
        public static BsonValue MAX(IEnumerable<BsonValue> values)
        {
            var max = BsonValue.MinValue;

            foreach (var value in values)
            {
                if (value.CompareTo(max) >= 0)
                {
                    max = value;
                }
            }

            return max == BsonValue.MinValue ? BsonValue.MaxValue : max;
        }

        /// <summary>
        /// Returns first value from an list of values (scan all source)
        /// </summary>
        public static BsonValue FIRST(IEnumerable<BsonValue> values)
        {
            return values.FirstOrDefault();
        }

        /// <summary>
        /// Returns last value from an list of values
        /// </summary>
        public static BsonValue LAST(IEnumerable<BsonValue> values)
        {
            return values.LastOrDefault();
        }

        /// <summary>
        /// Find average value from all values (number values only). Return a single value
        /// </summary>
        public static BsonValue AVG(IEnumerable<BsonValue> values)
        {
            var sum = new BsonValue(0);
            var count = 0;

            foreach (var value in values.Where(x => x.IsNumber))
            {
                sum += value;
                count++;
            }

            if (count > 0)
            {
                return sum / count;
            }
            else
            {
                return 0;
            }
        }

        /// <summary>
        /// Sum all values (number values only). Return a single value
        /// </summary>
        public static BsonValue SUM(IEnumerable<BsonValue> values)
        {
            var sum = new BsonValue(0);

            foreach (var value in values.Where(x => x.IsNumber))
            {
                sum += value;
            }

            return sum;
        }

        /// <summary>
        /// Return "true" if inner collection contains any result
        /// ANY($.items[*])
        /// </summary>
        public static BsonValue ANY(IEnumerable<BsonValue> values)
        {
            return values.Any(); 
        }
    }
}
