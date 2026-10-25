namespace LiteDB.Tests.Expressions
{
    internal static class SqlLikeReference
    {
        // Independent prefix-reachability specification, deliberately using the
        // original one-character string comparison instead of the optimized helper.
        internal static bool Match(string value, string pattern, Collation collation)
        {
            var matches = new bool[value.Length + 1, pattern.Length + 1];
            matches[0, 0] = true;
            for (var p = 1; p <= pattern.Length; p++)
            {
                var token = pattern[p - 1];
                if (token == '%') matches[0, p] = matches[0, p - 1];
                for (var v = 1; v <= value.Length; v++)
                {
                    matches[v, p] = token == '%' ? matches[v, p - 1] || matches[v - 1, p] :
                        matches[v - 1, p - 1] && (token == '_' ||
                        collation.Compare(value[v - 1].ToString(), token.ToString()) == 0);
                }
            }
            return matches[value.Length, pattern.Length];
        }
    }
}
