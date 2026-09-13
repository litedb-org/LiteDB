using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using static LiteDB.Constants;

namespace LiteDB
{
    public class JsonWriter
    {
        private readonly static IFormatProvider _numberFormat = CultureInfo.InvariantCulture.NumberFormat;

        private readonly TextWriter _writer;
        private int _indent;
        private string _spacer = "";

        /// <summary>
        /// Get/Set indent size
        /// </summary>
        public int Indent { get; set; } = 4;

        /// <summary>
        /// Get/Set if writer must print pretty (with new line/indent)
        /// </summary>
        public bool Pretty { get; set; } = false;

        public JsonWriter(TextWriter writer)
        {
            _writer = writer;
        }

        /// <summary>
        /// Serialize value into text writer
        /// </summary>
        public void Serialize(BsonValue value)
        {
            _indent = 0;
            _spacer = this.Pretty ? " " : "";

            this.WriteValue(value ?? BsonValue.Null);
        }

        private void WriteValue(BsonValue value)
        {
            // use direct cast to better performance
            switch (value.Type)
            {
                case BsonType.Null:
                    _writer.Write("null");
                    break;

                case BsonType.Array:
                    this.WriteArray(value.AsArray);
                    break;

                case BsonType.Document:
                    this.WriteObject(value.AsDocument);
                    break;

                case BsonType.Boolean:
                    _writer.Write(value.AsBoolean.ToString().ToLower());
                    break;

                case BsonType.String:
                    this.WriteString(value.AsString);
                    break;

                case BsonType.Int32:
                    _writer.Write(value.AsInt32.ToString(_numberFormat));
                    break;

                case BsonType.Double:
                    var d = value.AsDouble;

                    if (double.IsNaN(d) || double.IsNegativeInfinity(d) || double.IsPositiveInfinity(d))
                    {
                        _writer.Write("null");
                    }
                    else
                    {
                        _writer.Write(value.AsDouble.ToString("0.0########", _numberFormat));
                    }

                    break;

                case BsonType.Binary:
                    var bytes = value.AsBinary;
                    this.WriteExtendDataType("$binary", Convert.ToBase64String(bytes, 0, bytes.Length));
                    break;

                case BsonType.ObjectId:
                    this.WriteExtendDataType("$oid", value.AsObjectId.ToString());
                    break;

                case BsonType.Guid:
                    this.WriteExtendDataType("$guid", value.AsGuid.ToString());
                    break;

                case BsonType.DateTime:
                    this.WriteExtendDataType("$date", value.AsDateTime.ToUniversalTime().ToString("o"));
                    break;

                case BsonType.Int64:
                    this.WriteExtendDataType("$numberLong", value.AsInt64.ToString(_numberFormat));
                    break;

                case BsonType.Decimal:
                    this.WriteExtendDataType("$numberDecimal", value.AsDecimal.ToString(_numberFormat));
                    break;

                case BsonType.MinValue:
                    this.WriteExtendDataType("$minValue", "1");
                    break;

                case BsonType.MaxValue:
                    this.WriteExtendDataType("$maxValue", "1");
                    break;
            }
        }

        private void WriteObject(BsonDocument obj)
        {
            var length = obj.Keys.Count();
            var hasData = length > 0;

            this.WriteStartBlock("{", hasData);

            var index = 0;

            foreach (var el in obj.GetElements())
            {
                this.WriteKeyValue(el.Key, el.Value, index++ < length - 1);
            }

            this.WriteEndBlock("}", hasData);
        }

        private void WriteArray(BsonArray arr)
        {
            var hasData = arr.Count > 0;

            this.WriteStartBlock("[", hasData);

            for (var i = 0; i < arr.Count; i++)
            {
                var item = arr[i];

                // do not do this tests if is not pretty format - to better performance
                if (this.Pretty && item != null)
                {
                    if (!((item.IsDocument && item.AsDocument.Keys.Any()) || (item.IsArray && item.AsArray.Count > 0)))
                    {
                        this.WriteIndent();
                    }
                }

                this.WriteValue(item ?? BsonValue.Null);

                if (i < arr.Count - 1)
                {
                    _writer.Write(',');
                }
                this.WriteNewLine();
            }

            this.WriteEndBlock("]", hasData);
        }

        private void WriteString(string s)
        {
            _writer.Write('\"');
            int l = s.Length;
            for (var index = 0; index < l; index++)
            {
                var c = s[index];
                switch (c)
                {
                    case '\"':
                        _writer.Write("\\\"");
                        break;

                    case '\\':
                        _writer.Write("\\\\");
                        break;

                    case '\b':
                        _writer.Write("\\b");
                        break;

                    case '\f':
                        _writer.Write("\\f");
                        break;

                    case '\n':
                        _writer.Write("\\n");
                        break;

                    case '\r':
                        _writer.Write("\\r");
                        break;

                    case '\t':
                        _writer.Write("\\t");
                        break;

                    default:
                        switch (CharUnicodeInfo.GetUnicodeCategory(c))
                        {
                            case UnicodeCategory.UppercaseLetter:
                            case UnicodeCategory.LowercaseLetter:
                            case UnicodeCategory.TitlecaseLetter:
                            case UnicodeCategory.OtherLetter:
                            case UnicodeCategory.DecimalDigitNumber:
                            case UnicodeCategory.LetterNumber:
                            case UnicodeCategory.OtherNumber:
                            case UnicodeCategory.SpaceSeparator:
                            case UnicodeCategory.ConnectorPunctuation:
                            case UnicodeCategory.DashPunctuation:
                            case UnicodeCategory.OpenPunctuation:
                            case UnicodeCategory.ClosePunctuation:
                            case UnicodeCategory.InitialQuotePunctuation:
                            case UnicodeCategory.FinalQuotePunctuation:
                            case UnicodeCategory.OtherPunctuation:
                            case UnicodeCategory.MathSymbol:
                            case UnicodeCategory.CurrencySymbol:
                            case UnicodeCategory.ModifierSymbol:
                            case UnicodeCategory.OtherSymbol:
                                _writer.Write(c);
                                break;
                            default:
                                _writer.Write("\\u");
                                _writer.Write(((int)c).ToString("x04"));
                                break;
                        }
                        break;
                }
            }
            _writer.Write('\"');
        }

        private void WriteExtendDataType(string type, string value)
        {
            // format: { "$type": "string-value" }
            // no string.Format to better performance
            _writer.Write("{\"");
            _writer.Write(type);
            _writer.Write("\":");
            _writer.Write(_spacer);
            _writer.Write("\"");
            _writer.Write(value);
            _writer.Write("\"}");
        }

        private void WriteKeyValue(string key, BsonValue value, bool comma)
        {
            this.WriteIndent();

            _writer.Write('\"');
            _writer.Write(key);
            _writer.Write("\":");

            // do not do this tests if is not pretty format - to better performance
            if (this.Pretty)
            {
                _writer.Write(' ');

                if (value != null && ((value.IsDocument && value.AsDocument.Keys.Any()) || (value.IsArray && value.AsArray.Count > 0)))
                {
                    this.WriteNewLine();
                }
            }

            this.WriteValue(value ?? BsonValue.Null);

            if (comma)
            {
                _writer.Write(',');
            }

            this.WriteNewLine();
        }

        private void WriteStartBlock(string str, bool hasData)
        {
            if (hasData)
            {
                this.WriteIndent();
                _writer.Write(str);
                this.WriteNewLine();
                _indent++;
            }
            else
            {
                _writer.Write(str);
            }
        }

        private void WriteEndBlock(string str, bool hasData)
        {
            if (hasData)
            {
                _indent--;
                this.WriteIndent();
                _writer.Write(str);
            }
            else
            {
                _writer.Write(str);
            }
        }

        private void WriteNewLine()
        {
            if (this.Pretty)
            {
                _writer.WriteLine();
            }
        }

        private void WriteIndent()
        {
            if (this.Pretty)
            {
                _writer.Write("".PadRight(_indent * this.Indent, ' '));
            }
        }
    }
}