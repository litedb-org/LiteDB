using System;
using System.Text;

namespace LiteDB
{
    internal partial class Tokenizer
    {
        /// <summary>
        /// Read a string removing open and close " or '.
        /// </summary>
        private string ReadString(char quote)
        {
            var sb = new StringBuilder();
            this.ReadChar();

            while (_char != quote && !_eof)
            {
                if (_char == '\\')
                {
                    this.ReadChar();
                    switch (_char)
                    {
                        case var escapedQuote when escapedQuote == quote: sb.Append(quote); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            var c1 = this.ReadChar();
                            var c2 = this.ReadChar();
                            var c3 = this.ReadChar();
                            var c4 = this.ReadChar();
                            if (StrictStrings && (!IsHex(c1) || !IsHex(c2) || !IsHex(c3) || !IsHex(c4)))
                                throw new FormatException($"Invalid Unicode escape at position {this.Position}.");
                            sb.Append((char)ParseUnicode(c1, c2, c3, c4));
                            break;
                        default:
                            if (StrictStrings)
                                throw new FormatException($"Invalid escape sequence \\{_char} at position {this.Position}.");
                            break;
                    }
                }
                else
                {
                    sb.Append(_char);
                }
                this.ReadChar();
            }

            if (StrictStrings && _eof && _char != quote)
                throw new FormatException($"Unterminated string at position {this.Position}.");
            this.ReadChar();
            return sb.ToString();
        }

        private static bool IsHex(char value) => value >= '0' && value <= '9' ||
            value >= 'A' && value <= 'F' || value >= 'a' && value <= 'f';
    }
}
