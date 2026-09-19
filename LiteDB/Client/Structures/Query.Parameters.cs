using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LiteDB
{
    public partial class Query
    {
        internal static BsonExpression CreateValueQuery(string field, string operation, params BsonValue[] values)
        {
            // Field accepts an expression, so do not capture its existing unbound
            // parameter names when supplying the helper's comparison values.
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var tokenizer = new Tokenizer(field);
            Token token;
            while ((token = tokenizer.ReadToken(false)).Type != TokenType.EOF)
            {
                if (token.Type != TokenType.At) continue;
                var next = tokenizer.LookAhead(false);
                if (next.Type == TokenType.Word || next.Type == TokenType.Int)
                    names.Add(tokenizer.ReadToken(false).Value);
            }
            var parameters = new BsonDocument();
            var placeholders = new object[values.Length];
            var index = 0;
            for (var i = 0; i < values.Length; i++)
            {
                string name;
                do { name = "__queryValue" + index++; } while (!names.Add(name));
                placeholders[i] = "@" + name;
                parameters[name] = SnapshotQueryValue(values[i]);
            }
            return BsonExpression.Create(field + " " + string.Format(CultureInfo.InvariantCulture, operation, placeholders), parameters);
        }

        private static BsonValue SnapshotQueryValue(BsonValue value)
        {
            if (value == null) return BsonValue.Null;
            if (value.IsArray) return new BsonArray(value.AsArray.Select(SnapshotQueryValue));
            if (value.IsDocument)
            {
                var copy = new BsonDocument();
                foreach (var item in value.AsDocument) copy[item.Key] = SnapshotQueryValue(item.Value);
                return copy;
            }
            if (value.IsBinary) return new BsonValue((byte[])value.AsBinary.Clone());
            if (value.Type == BsonType.Vector) return new BsonVector((float[])value.AsVector.Clone());
            return value;
        }
    }
}
