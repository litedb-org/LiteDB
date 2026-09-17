namespace LiteDB.Tests.Expressions
{
    // Frozen pre-optimization matcher: differential oracle for allocation changes.
    internal static class LegacySqlLike
    {
        internal static bool? Match(string str, string pattern, Collation collation)
        {
            // The historical matcher can revisit states without making progress.
            // Bound this test oracle; comparison equivalence covers those code units
            // separately without asking either matcher to finish that old loop.
            var budget = 10000;
            var isMatch = true;
            var isWildCardOn = false;
            var isCharWildCardOn = false;
            var isCharSetOn = false;
            var isNotCharSetOn = false;
            var endOfPattern = false;
            var lastWildCard = -1;
            var patternIndex = 0;
            var p = '\0';

            for (var i = 0; i < str.Length; i++)
            {
                if (--budget == 0) return null;
                var c = str[i];

                endOfPattern = (patternIndex >= pattern.Length);

                if (!endOfPattern)
                {
                    p = pattern[patternIndex];

                    if (!isWildCardOn && p == '%')
                    {
                        lastWildCard = patternIndex;
                        isWildCardOn = true;

                        while (patternIndex < pattern.Length && pattern[patternIndex] == '%')
                        {
                            patternIndex++;
                        }

                        if (patternIndex >= pattern.Length)
                        {
                            p = '\0';
                        }
                        else
                        {
                            p = pattern[patternIndex];
                        }
                    }
                    else if (p == '_')
                    {
                        isCharWildCardOn = true;
                        patternIndex++;
                    }
                }

                if (isWildCardOn)
                {
                    if (collation.Compare(c.ToString(), p.ToString()) == 0)
                    {
                        isWildCardOn = false;
                        patternIndex++;
                    }
                }
                else if (isCharWildCardOn)
                {
                    isCharWildCardOn = false;
                }
                else if (isCharSetOn || isNotCharSetOn)
                {
                    //var charMatch = (set.Contains(char.ToUpper(c))); // -- always "false" - remove [abc] support
                    //if ((isNotCharSetOn && charMatch) || (isCharSetOn && !charMatch))

                    if (isCharSetOn)
                    {
                        if (lastWildCard >= 0)
                        {
                            patternIndex = lastWildCard;
                        }
                        else
                        {
                            isMatch = false;
                            break;
                        }
                    }

                    isNotCharSetOn = isCharSetOn = false;
                }
                else
                {
                    if (collation.Compare(c.ToString(), p.ToString()) == 0)
                    {
                        patternIndex++;
                    }
                    else
                    {
                        if (lastWildCard >= 0)
                        {
                            int back = patternIndex - lastWildCard - 1;
                            i -= back;
                            patternIndex = lastWildCard;
                        }
                        else
                        {
                            isMatch = false;
                            break;
                        }
                    }
                }
            }

            endOfPattern = (patternIndex >= pattern.Length);

            if (isMatch && !endOfPattern)
            {
                var isOnlyWildCards = true;

                for (var i = patternIndex; i < pattern.Length; i++)
                {
                    if (pattern[i] != '%')
                    {
                        isOnlyWildCards = false;
                        break;
                    }
                }

                if (isOnlyWildCards) endOfPattern = true;
            }

            return isMatch && endOfPattern;
        }

    }
}
