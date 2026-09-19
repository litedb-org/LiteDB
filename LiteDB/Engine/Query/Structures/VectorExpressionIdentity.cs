using System;

namespace LiteDB.Engine
{
    internal static class VectorExpressionIdentity
    {
        internal static bool HasSameSource(string left, string right)
        {
            if (string.Equals(left, right, StringComparison.Ordinal)) return true;
            if (!string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) return false;

            // Canonical sources may differ in field-name casing, but folding string
            // literals can select a different computed vector, even under the same collation.
            var leftTokens = new Tokenizer(left);
            var rightTokens = new Tokenizer(right);
            var previous = TokenType.EOF;
            var beforePrevious = TokenType.EOF;
            while (true)
            {
                var leftToken = leftTokens.ReadToken();
                var rightToken = rightTokens.ReadToken();
                if (leftToken.Type != rightToken.Type) return false;
                if (leftToken.Type == TokenType.EOF) return true;

                // ReadField emits .["complex-name"] for quoted member identifiers.
                // Array contents and other string values remain case-sensitive.
                var memberName = beforePrevious == TokenType.Period && previous == TokenType.OpenBracket;
                var comparison = leftToken.Type == TokenType.String && !memberName
                    ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                if (!string.Equals(leftToken.Value, rightToken.Value, comparison)) return false;
                beforePrevious = previous;
                previous = leftToken.Type;
            }
        }
    }
}
