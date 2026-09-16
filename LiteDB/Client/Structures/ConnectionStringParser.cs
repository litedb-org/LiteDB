using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace LiteDB
{
    internal static class ConnectionStringParser
    {
        public static Dictionary<string, string> Parse(string text, ISet<string> quotedValues = null)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var position = 0;

            while (position < text.Length)
            {
                SkipWhitespace(text, ref position);
                if (position == text.Length) break;
                if (text[position] == ';')
                {
                    throw new FormatException("Expected a connection option followed by '='.");
                }

                var start = position;
                while (position < text.Length && text[position] != '=' && text[position] != ';') position++;
                if (position == text.Length || text[position] != '=')
                {
                    throw new FormatException("Expected a connection option followed by '='.");
                }

                var key = Regex.Replace(text.Substring(start, position - start).Trim(), @"\s+", " ");
                if (key.Length == 0) throw new FormatException("Expected a connection option name.");
                position++;
                values[key] = ReadValue(text, ref position, out var wasQuoted);

                if (wasQuoted)
                {
                    quotedValues?.Add(key);
                }
                else
                {
                    quotedValues?.Remove(key);
                }
            }

            return values;
        }

        private static string ReadValue(string text, ref int position, out bool wasQuoted)
        {
            SkipWhitespace(text, ref position);
            var quote = position < text.Length && (text[position] == '"' || text[position] == '\'')
                ? text[position++] : '\0';
            wasQuoted = quote != '\0';
            var value = new StringBuilder();

            while (position < text.Length)
            {
                var current = text[position];
                if (quote == '\0' && current == ';')
                {
                    position++;
                    return value.ToString().Trim();
                }

                if (current == '\\')
                {
                    var start = position;
                    while (position < text.Length && text[position] == '\\') position++;
                    var count = position - start;
                    var escapesQuote = position < text.Length &&
                        (text[position] == quote || (quote == '\0' && text[position] == '"'));
                    if (escapesQuote)
                    {
                        value.Append('\\', count / 2);
                        if (count % 2 != 0)
                        {
                            value.Append(text[position++]);
                        }
                    }
                    else
                    {
                        value.Append('\\', count);
                    }
                    continue;
                }

                if (quote != '\0' && current == quote)
                {
                    position++;
                    SkipWhitespace(text, ref position);
                    if (position < text.Length && text[position] != ';')
                    {
                        throw new FormatException("Expected ';' after a quoted connection value.");
                    }
                    if (position < text.Length) position++;
                    return value.ToString();
                }

                if (quote == '\0' && current == '"')
                {
                    throw new FormatException("A quote inside an unquoted connection value must be escaped.");
                }

                value.Append(current);
                position++;
            }

            if (quote != '\0') throw new FormatException("Unterminated quoted connection value.");
            return value.ToString().Trim();
        }

        private static void SkipWhitespace(string text, ref int position)
        {
            while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
        }
    }
}
