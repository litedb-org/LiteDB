using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using static LiteDB.Constants;

namespace LiteDB
{
    /// <summary>
    /// Implement how database will compare to order by/find strings according defined culture/compare options
    /// If not set, default is CurrentCulture with IgnoreCase
    /// </summary>
    public class Collation : IComparer<BsonValue>, IComparer<string>, IEqualityComparer<BsonValue>
    {
        private readonly CompareInfo _compareInfo;

        public Collation(string collation)
        {
            var parts = collation.Split('/');
            var culture = parts[0];
            var sortOptions = parts.Length > 1 ? 
                (CompareOptions)Enum.Parse(typeof(CompareOptions), parts[1]) : 
                CompareOptions.None;

            this.LCID = LiteDB.LCID.GetLCID(culture);
            this.SortOptions = sortOptions;
            this.Culture = new CultureInfo(culture);

            _compareInfo = this.Culture.CompareInfo;
        }

        public Collation(int lcid, CompareOptions sortOptions)
        {
            this.LCID = lcid;
            this.SortOptions = sortOptions;
            this.Culture = LiteDB.LCID.GetCulture(lcid);

            _compareInfo = this.Culture.CompareInfo;
        }

        public static Collation Default = new Collation(LiteDB.LCID.Current, CompareOptions.IgnoreCase);

        public static Collation Binary = new Collation(127 /* Invariant */, CompareOptions.Ordinal);

        /// <summary>
        /// Get LCID code from culture
        /// </summary>
        public int LCID { get; }

        /// <summary>
        /// Get database language culture
        /// </summary>
        public CultureInfo Culture { get; }

        /// <summary>
        /// Get options to how string should be compared in sort
        /// </summary>
        public CompareOptions SortOptions { get; }

        /// <summary>
        /// Compare 2 string values using current culture/compare options
        /// </summary>
        public int Compare(string left, string right)
        {
            var result = _compareInfo.Compare(left, right, this.SortOptions);

            return result < 0 ? -1 : result > 0 ? +1 : 0;
        }

        public int Compare(BsonValue left, BsonValue rigth)
        {
            return left.CompareTo(rigth, this);
        }

        public bool Equals(BsonValue x, BsonValue y)
        {
            return this.Compare(x, y) == 0;
        }

        public int GetHashCode(BsonValue obj)
        {
            return obj.GetHashCode();
        }

        public override string ToString()
        {
            return this.Culture.Name + "/" + this.SortOptions.ToString();
        }
    }
}