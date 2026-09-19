using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

using static LiteDB.Constants;

namespace LiteDB
{
    internal static class StringExtensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsNullOrWhiteSpace(this string str)
        {
            return string.IsNullOrWhiteSpace(str);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsNullOrEmpty(this string str)
        {
            return string.IsNullOrEmpty(str);
        }

        /// <summary>
        /// Test if string is simple word pattern ([a-Z$_])
        /// </summary>
        public static bool IsWord(this string str)
        {
            if (string.IsNullOrWhiteSpace(str)) return false;

            for (var i = 0; i < str.Length; i++)
            {
                if (!Tokenizer.IsWordChar(str[i], i == 0)) return false;
            }

            return true;
        }

        /// <summary>
        /// Match SQL LIKE patterns: percent matches any sequence and underscore one character.
        /// </summary>
        public static bool SqlLike(this string str, string pattern, Collation collation)
        {
            var valueIndex = 0;
            var patternIndex = 0;
            var wildcardIndex = -1;
            var wildcardValueIndex = 0;

            while (valueIndex < str.Length)
            {
                if (patternIndex < pattern.Length && pattern[patternIndex] == '%')
                {
                    wildcardIndex = patternIndex++;
                    wildcardValueIndex = valueIndex;
                }
                else if (patternIndex < pattern.Length &&
                    (pattern[patternIndex] == '_' ||
                     collation.Compare(str[valueIndex].ToString(), pattern[patternIndex].ToString()) == 0))
                {
                    valueIndex++;
                    patternIndex++;
                }
                else if (wildcardIndex >= 0)
                {
                    // Retry after the last percent, consuming one more input character each time.
                    patternIndex = wildcardIndex + 1;
                    valueIndex = ++wildcardValueIndex;
                }
                else
                {
                    return false;
                }
            }

            while (patternIndex < pattern.Length && pattern[patternIndex] == '%') patternIndex++;
            return patternIndex == pattern.Length;
        }

        /// <summary>
        /// Get first string before any `%` or `_` ... used to index startswith - out if has more string pattern after found wildcard
        /// </summary>
        public static string SqlLikeStartsWith(this string str, out bool hasMore)
        {
            var i = 0;
            var len = str.Length;
            var c = '\0';

            while (i < len)
            {
                c = str[i];

                if (c == '%' || c == '_')
                {
                    break;
                }

                i++;
            }

            hasMore = i < len && (c != '%' || i < len - 1);

            return str.Substring(0, i);
        }
    }
}