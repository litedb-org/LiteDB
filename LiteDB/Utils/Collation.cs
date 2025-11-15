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
    /// Defines how the database compares and orders strings according to culture and comparison options.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Collation determines string comparison behavior for sorting (ORDER BY) and equality operations (WHERE, FIND).
    /// If not explicitly set, the default is the current culture with case-insensitive comparison.
    /// </para>
    /// <para>
    /// Collation is specified as a string in the format: <c>culture/options</c>, where culture is a culture name (e.g., "en-US")
    /// and options is a <see cref="CompareOptions"/> value (e.g., "IgnoreCase").
    /// </para>
    /// </remarks>
    public class Collation : IComparer<BsonValue>, IComparer<string>, IEqualityComparer<BsonValue>
    {
        private readonly CompareInfo _compareInfo;

        /// <summary>
        /// Initializes a new instance of the <see cref="Collation"/> class from a collation string.
        /// </summary>
        /// <param name="collation">
        /// A string in the format <c>culture/options</c> (e.g., "en-US/IgnoreCase") or just culture name (e.g., "en-US").
        /// <para>If options are omitted, <see cref="CompareOptions.None"/> is used.</para>
        /// </param>
        /// <exception cref="ArgumentException">Thrown when the culture name or compare options are invalid.</exception>
        /// <example>
        /// <code>
        /// var collation1 = new Collation("en-US/IgnoreCase");
        /// var collation2 = new Collation("pt-BR");
        /// var collation3 = new Collation("de-DE/IgnoreCase,IgnoreSymbols");
        /// </code>
        /// </example>
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

        /// <summary>
        /// Initializes a new instance of the <see cref="Collation"/> class using a locale identifier and comparison options.
        /// </summary>
        /// <param name="lcid">The locale identifier (LCID) for the culture.</param>
        /// <param name="sortOptions">The string comparison options to use.</param>
        public Collation(int lcid, CompareOptions sortOptions)
        {
            this.LCID = lcid;
            this.SortOptions = sortOptions;
            this.Culture = LiteDB.LCID.GetCulture(lcid);

            _compareInfo = this.Culture.CompareInfo;
        }

        /// <summary>
        /// Gets the default collation using the current culture with case-insensitive comparison.
        /// </summary>
        public static Collation Default = new Collation(LiteDB.LCID.Current, CompareOptions.IgnoreCase);

        /// <summary>
        /// Gets a binary collation using the invariant culture with ordinal comparison (culture-insensitive, case-sensitive).
        /// </summary>
        /// <remarks>
        /// Binary collation performs byte-by-byte comparison and is the fastest comparison method.
        /// Use this when culture-specific sorting is not required.
        /// </remarks>
        public static Collation Binary = new Collation(127 /* Invariant */, CompareOptions.Ordinal);

        /// <summary>
        /// Gets the locale identifier (LCID) of the culture used.
        /// </summary>
        public int LCID { get; }

        /// <summary>
        /// Gets the <see cref="CultureInfo"/> used.
        /// </summary>
        public CultureInfo Culture { get; }

        /// <summary>
        /// Gets the <see cref="CompareOptions"/> that define how strings are compared during sorting.
        /// </summary>
        public CompareOptions SortOptions { get; }

        /// <summary>
        /// Compares two string values using the current culture and comparison options.
        /// </summary>
        /// <param name="left">The first string to compare.</param>
        /// <param name="right">The second string to compare.</param>
        /// <returns>
        /// A signed integer that indicates the relative order: -1 if <paramref name="left"/> is less than <paramref name="right"/>,
        /// 0 if they are equal, or 1 if <paramref name="left"/> is greater than <paramref name="right"/>.
        /// </returns>
        public int Compare(string left, string right)
        {
            var result = _compareInfo.Compare(left, right, this.SortOptions);

            return result < 0 ? -1 : result > 0 ? +1 : 0;
        }

        /// <summary>
        /// Compares two <see cref="BsonValue"/> instances using the current collation.
        /// </summary>
        /// <param name="left">The first <see cref="BsonValue"/> to compare.</param>
        /// <param name="right">The second <see cref="BsonValue"/> to compare.</param>
        /// <returns>
        /// A signed integer that indicates the relative order: -1 if <paramref name="left"/> is less than <paramref name="right"/>,
        /// 0 if they are equal, or 1 if <paramref name="left"/> is greater than <paramref name="right"/>.
        /// </returns>
        public int Compare(BsonValue left, BsonValue right)
        {
            return left.CompareTo(right, this);
        }

        /// <summary>
        /// Determines whether two <see cref="BsonValue"/> instances are equal using the current collation.
        /// </summary>
        /// <param name="x">The first <see cref="BsonValue"/> to compare.</param>
        /// <param name="y">The second <see cref="BsonValue"/> to compare.</param>
        /// <returns><see langword="true"/> if the values are equal; otherwise, <see langword="false"/>.</returns>
        public bool Equals(BsonValue x, BsonValue y)
        {
            return this.Compare(x, y) == 0;
        }

        /// <summary>
        /// Returns a hash code for the specified <see cref="BsonValue"/>.
        /// </summary>
        /// <param name="obj">The <see cref="BsonValue"/> for which to get a hash code.</param>
        /// <returns>A hash code for the specified object.</returns>
        public int GetHashCode(BsonValue obj)
        {
            return obj.GetHashCode();
        }

        /// <summary>
        /// Returns a string representation of this collation in the format <c>culture/options</c>.
        /// </summary>
        /// <returns>A string in the format <c>culture/options</c> (e.g., "en-US/IgnoreCase").</returns>
        public override string ToString()
        {
            return this.Culture.Name + "/" + this.SortOptions.ToString();
        }
    }
}