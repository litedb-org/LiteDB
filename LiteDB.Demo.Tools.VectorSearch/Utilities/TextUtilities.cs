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

        private static readonly char[] _chunkBreakCharacters = { '\n', ' ', '\t' };

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

        public static IEnumerable<string> SplitIntoChunks(string content, int chunkLength, int chunkOverlap)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                yield break;
            }

            if (chunkLength <= 0)
            {
                yield break;
            }

            if (chunkOverlap < 0 || chunkOverlap >= chunkLength)
            {
                throw new ArgumentOutOfRangeException(nameof(chunkOverlap), chunkOverlap, "Chunk overlap must be non-negative and smaller than the chunk length.");
            }

            var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');

            var step = chunkLength - chunkOverlap;
            var position = 0;

            if (step <= 0)
            {
                yield break;
            }

            while (position < normalized.Length)
            {
                var remaining = normalized.Length - position;
                var take = Math.Min(chunkLength, remaining);
                var window = normalized.Substring(position, take);

                if (take == chunkLength && position + take < normalized.Length)
                {
                    var lastBreak = window.LastIndexOfAny(_chunkBreakCharacters);
                    if (lastBreak >= step)
                    {
                        window = window[..lastBreak];
                        take = window.Length;
                    }
                }

                var chunk = window.Trim();
                if (!string.IsNullOrWhiteSpace(chunk))
                {
                    yield return chunk;
                }

                if (position + take >= normalized.Length)
                {
                    yield break;
                }

                position += step;
            }
        }
    }
}
