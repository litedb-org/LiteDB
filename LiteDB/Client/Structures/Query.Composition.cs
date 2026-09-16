using System;
using System.Collections.Generic;
using System.Text;

namespace LiteDB
{
    public partial class Query
    {
        private static BsonExpression Compose(BsonExpression left, BsonExpression right, string operation)
        {
            var parameters = new BsonDocument();
            var groupKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var index = 0;
            var leftSource = CopyExpression(left, parameters, groupKeys, ref index);
            var rightSource = CopyExpression(right, parameters, groupKeys, ref index);
            var result = BsonExpression.Create($"({leftSource} {operation} {rightSource})", parameters);
            result.GroupKeyAliases = groupKeys;
            return result;
        }

        private static string CopyExpression(BsonExpression expression, BsonDocument parameters,
            HashSet<string> groupKeys, ref int index)
        {
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (expression.Parameters != null)
            {
                foreach (var parameter in expression.Parameters)
                {
                    var name = "q" + index++;
                    names[parameter.Key] = name;
                    parameters[name] = parameter.Value;
                }
            }

            var source = expression.Source;
            var tokenizer = new Tokenizer(source);
            var builder = new StringBuilder();
            var offset = 0;
            Token token;
            while ((token = tokenizer.ReadToken(false)).Type != TokenType.EOF)
            {
                if (token.Type != TokenType.At) continue;
                var next = tokenizer.LookAhead(false);
                if (next.Type != TokenType.Word && next.Type != TokenType.Int) continue;
                tokenizer.ReadToken(false);
                if (!names.TryGetValue(next.Value, out var name))
                {
                    // Rename unbound parameters too so another operand cannot bind
                    // them accidentally. Standalone @ current-item paths stay intact.
                    names[next.Value] = name = "q" + index++;
                }
                if (next.Value.Equals("key", StringComparison.OrdinalIgnoreCase) ||
                    expression.GroupKeyAliases?.Contains(next.Value) == true) groupKeys.Add(name);

                // At.Position is one-based, so it is the zero-based start of the
                // following name. Tokenization protects quoted strings/field names.
                var start = checked((int)token.Position);
                builder.Append(source, offset, start - offset);
                builder.Append(name);
                offset = start + next.Value.Length;
            }
            builder.Append(source, offset, source.Length - offset);
            return builder.ToString();
        }
    }
}
