using System;

namespace LiteDB.Shell
{
    internal static class StringExtensions
    {
        public static string ThrowIfEmpty(this string str, string message)
        {
            if (string.IsNullOrEmpty(str) || str.Trim().Length == 0)
            {
                throw new ArgumentException(message);
            }

            return str;
        }

        public static string TrimToNull(this string str)
        {
            var v = str.Trim();

            return v.Length == 0 ? null : v;
        }

        public static string MaxLength(this string str, int len)
        {
            return len == 0 || str.Length < len ?
                str :
                str.Substring(0, len - 3) + "...";
        }
    }
}