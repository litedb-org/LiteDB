using System;
using System.Text;
using System.Text.RegularExpressions;

namespace LiteDB.Shell
{
    /// <summary>
    /// A StringScanner is state machine used in text parsers based on regular expressions
    /// </summary>
    public class StringScanner
    {
        public string Source { get; private set; }
        public int Index { get; set; }

        /// <summary>
        /// Initialize scanner with a string to be parsed
        /// </summary>
        public StringScanner(string source)
        {
            this.Source = source;
            this.Index = 0;
        }

        public override string ToString()
        {
            return this.HasTerminated ? "<EOF>" : this.Source.Substring(this.Index);
        }

        /// <summary>
        /// Reset cursor position
        /// </summary>
        public void Reset()
        {
            this.Index = 0;
        }

        /// <summary>
        /// Skip cursor position in string source
        /// </summary>
        public void Seek(int length)
        {
            this.Index += length;
        }

        /// <summary>
        /// Indicate that cursor is EOF
        /// </summary>
        public bool HasTerminated
        {
            get { return this.Index >= this.Source.Length; }
        }

        /// <summary>
        /// Scan in current cursor position for this patterns. If found, returns string and run with cursor
        /// </summary>
        public string Scan(string pattern)
        {
            return this.Scan(new Regex((pattern.StartsWith("^") ? "" : "^") + pattern, RegexOptions.IgnorePatternWhitespace));
        }

        /// <summary>
        /// Scan in current cursor position for this patterns. If found, returns string and run with cursor
        /// </summary>
        public string Scan(Regex regex)
        {
            var match = regex.Match(this.Source, this.Index, this.Source.Length - this.Index);

            if (match.Success)
            {
                this.Index += match.Length;
                return match.Value;
            }
            else
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Scan pattern and returns group string index 1 based
        /// </summary>
        public string Scan(string pattern, int group)
        {
            return this.Scan(new Regex((pattern.StartsWith("^") ? "" : "^") + pattern, RegexOptions.IgnorePatternWhitespace), group);
        }

        public string Scan(Regex regex, int group)
        {
            var match = regex.Match(this.Source, this.Index, this.Source.Length - this.Index);

            if (match.Success)
            {
                this.Index += match.Length;
                return group >= match.Groups.Count ? "" : match.Groups[group].Value;
            }
            else
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Match if pattern is true in current cursor position. Do not change cursor position
        /// </summary>
        public bool Match(string pattern)
        {
            return this.Match(new Regex((pattern.StartsWith("^") ? "" : "^") + pattern, RegexOptions.IgnorePatternWhitespace));
        }

        /// <summary>
        /// Match if pattern is true in current cursor position. Do not change cursor position
        /// </summary>
        public bool Match(Regex regex)
        {
            var match = regex.Match(this.Source, this.Index, this.Source.Length - this.Index);
            return match.Success;
        }

        /// <summary>
        /// Throw syntax exception if not terminate string
        /// </summary>
        public void ThrowIfNotFinish()
        {
            this.Scan(@"\s*");

            if (!this.HasTerminated) throw new Exception("Unknow token after command");
        }
    }
}