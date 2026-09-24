using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LiteDB
{
    // Formatting is an output of construction, never an input to LINQ translation.
    internal static class BsonExpressionFormatter
    {
        internal static string Key(string name) => name.IsWord() ? name : JsonSerializer.Serialize(name);

        internal static string PathField(string name) => name.IsWord() ? name : "[" + JsonSerializer.Serialize(name) + "]";

        internal static string Constant(BsonValue value) => value.IsDouble ?
            Double(value.AsDouble) :
            value.IsInt64 ? value.AsInt64.ToString(CultureInfo.InvariantCulture) : JsonSerializer.Serialize(value);

        // Source is also the compiled-expression cache key, so distinct doubles need distinct
        // text: keep the short fixed form only when it parses back to the same bits.
        private static string Double(double value)
        {
            var text = value.ToString("0.0########", CultureInfo.InvariantCulture);
            if (double.IsNaN(value) || double.IsInfinity(value)) return text;

            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
                BitConverter.DoubleToInt64Bits(parsed) == BitConverter.DoubleToInt64Bits(value)
                ? text
                : JsonSerializer.Serialize(value);
        }

        internal static string Call(string name, IEnumerable<BsonExpression> arguments) =>
            name.ToUpperInvariant() + "(" + string.Join(",", arguments.Select(x => x.Source)) + ")";

        internal static string Binary(string operation, BsonExpression left, BsonExpression right) =>
            left.Source + operation + right.Source;

        internal static string Array(IEnumerable<BsonExpression> values) =>
            "[" + string.Join(",", values.Select(x => x.Source)) + "]";

        internal static string Document(KeyValuePair<string, BsonExpression>[] fields, BsonExpression[] values) =>
            "{" + string.Join(",", fields.Select((x, i) => Key(x.Key) + ":" + values[i].Source)) + "}";

        internal static string Function(string name, BsonExpression left, BsonExpression right, IEnumerable<BsonExpression> arguments) =>
            name + "(" + left.Source + (right == null ? "" : "=>" + right.Source) +
            string.Concat(arguments.Select(x => "," + x.Source)) + ")";
    }
}
