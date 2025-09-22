using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LiteDB.Demo.Tools.VectorSearch.Utilities
{
    internal static class TextUtilities
    {
        private static readonly HashSet<string> _supportedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".txt",
            ".md",
            ".markdown",
            ".mdown"
        };

        public static bool IsSupportedDocument(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var extension = Path.GetExtension(path);
            return !string.IsNullOrEmpty(extension) && _supportedExtensions.Contains(extension);
        }

        public static string ReadDocument(string path)
        {
            return File.ReadAllText(path);
        }

        public static string NormalizeForEmbedding(string content, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return string.Empty;
            }

            if (maxLength <= 0)
            {
                return string.Empty;
            }

            var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Trim();

            if (normalized.Length <= maxLength)
            {
                return normalized;
            }

            return normalized[..maxLength];
        }

        public static string BuildPreview(string content, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(content) || maxLength <= 0)
            {
                return string.Empty;
            }

            var collapsed = new StringBuilder(Math.Min(content.Length, maxLength));
            var previousWhitespace = false;

            foreach (var ch in content)
            {
                if (char.IsControl(ch) && ch != '\n' && ch != '\t')
                {
                    continue;
                }

                if (char.IsWhiteSpace(ch))
                {
                    if (!previousWhitespace)
                    {
                        collapsed.Append(' ');
                    }

                    previousWhitespace = true;
                }
                else
                {
                    previousWhitespace = false;
                    collapsed.Append(ch);
                }

                if (collapsed.Length >= maxLength)
                {
                    break;
                }
            }

            var preview = collapsed.ToString().Trim();
            return preview.Length <= maxLength ? preview : preview[..maxLength];
        }

        public static string ComputeContentHash(string content)
        {
            if (content == null)
            {
                return string.Empty;
            }

            using var sha256 = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(content);
            var hash = sha256.ComputeHash(bytes);

            return Convert.ToHexString(hash);
        }
    }
}
