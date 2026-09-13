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
        #region Year/Month/Day/Hour/Minute/Second

        /// <summary>
        /// Get year from date
        /// </summary>
        public static BsonValue YEAR(BsonValue value)
        {
            if (value.IsDateTime) return value.AsDateTime.Year;

            return BsonValue.Null;
        }

        /// <summary>
        /// Get month from date
        /// </summary>
        public static BsonValue MONTH(BsonValue value)
        {
            if (value.IsDateTime) return value.AsDateTime.Month;

            return BsonValue.Null;
        }

        /// <summary>
        /// Get day from date
        /// </summary>
        public static BsonValue DAY(BsonValue value)
        {
            if (value.IsDateTime) return value.AsDateTime.Day;

            return BsonValue.Null;

        }

        /// <summary>
        /// Get hour from date
        /// </summary>
        public static BsonValue HOUR(BsonValue value)
        {
            if (value.IsDateTime) return value.AsDateTime.Hour;

            return BsonValue.Null;
        }

        /// <summary>
        /// Get minute from date
        /// </summary>
        public static BsonValue MINUTE(BsonValue value)
        {
            if (value.IsDateTime) return value.AsDateTime.Minute;

            return BsonValue.Null;

        }

        /// <summary>
        /// Get seconds from date
        /// </summary>
        public static BsonValue SECOND(BsonValue value)
        {
            if (value.IsDateTime) return value.AsDateTime.Second;

            return BsonValue.Null;
        }

        #endregion

        #region Date Functions

        /// <summary>
        /// Add an interval to date. Use dateInterval: "y" (or "year"), "M" (or "month"), "d" (or "day"), "h" (or "hour"), "m" (or "minute"), "s" or ("second")
        /// </summary>
        public static BsonValue DATEADD(BsonValue dateInterval, BsonValue number, BsonValue value)
        {
            if (dateInterval.IsString && number.IsNumber && value.IsDateTime)
            {
                var datePart = dateInterval.AsString;
                var numb = number.AsInt32;
                var date = value.AsDateTime;

                datePart = datePart == "M" ? "month" : datePart.ToLower();

                if (datePart == "y" || datePart == "year") return date.AddYears(numb);
                else if (datePart == "month") return date.AddMonths(numb);
                else if (datePart == "d" || datePart == "day") return date.AddDays(numb);
                else if (datePart == "h" || datePart == "hour") return date.AddHours(numb);
                else if (datePart == "m" || datePart == "minute") return date.AddMinutes(numb);
                else if (datePart == "s" || datePart == "second") return date.AddSeconds(numb);
            }

            return BsonValue.Null;
        }

        /// <summary>
        /// Returns an interval about 2 dates. Use dateInterval: "y|year", "M|month", "d|day", "h|hour", "m|minute", "s|second"
        /// </summary>
        public static BsonValue DATEDIFF(BsonValue dateInterval, BsonValue starts, BsonValue ends)
        {
            if (dateInterval.IsString && starts.IsDateTime && ends.IsDateTime)
            { 
                var datePart = dateInterval.AsString;
                var start = starts.AsDateTime;
                var end = ends.AsDateTime;

                datePart = datePart == "M" ? "month" : datePart.ToLower();

                if (datePart == "y" || datePart == "year") return start.YearDifference(end);
                else if (datePart == "month") return start.MonthDifference(end);
                else if (datePart == "d" || datePart == "day") return Convert.ToInt32(Math.Truncate(end.Subtract(start).TotalDays));
                else if (datePart == "h" || datePart == "hour") return Convert.ToInt32(Math.Truncate(end.Subtract(start).TotalHours));
                else if (datePart == "m" || datePart == "minute") return Convert.ToInt32(Math.Truncate(end.Subtract(start).TotalMinutes));
                else if (datePart == "s" || datePart == "second") return Convert.ToInt32(Math.Truncate(end.Subtract(start).TotalSeconds));
            }

            return BsonValue.Null;
        }

        /// <summary>
        /// Convert UTC date into LOCAL date
        /// </summary>
        public static BsonValue TO_LOCAL(BsonValue date)
        {
            if (date.IsDateTime)
            {
                return date.AsDateTime.ToLocalTime();
            }

            return BsonValue.Null;
        }

        /// <summary>
        /// Convert LOCAL date into UTC date
        /// </summary>
        public static BsonValue TO_UTC(BsonValue date)
        {
            if (date.IsDateTime)
            {
                return date.AsDateTime.ToUniversalTime();
            }

            return BsonValue.Null;
        }

        #endregion
    }
}
