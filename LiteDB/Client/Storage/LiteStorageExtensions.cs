using System;
using System.IO;
using System.Text;

namespace LiteDB
{
    /// <summary>File storage extension methods that mimic <see cref="System.IO.File"/> methods.</summary>
    public static class LiteStorageExtensions
    {
        private static readonly Encoding Utf8NoPreamble = new UTF8Encoding(false);

        /// <summary>Opens a text file, reads all its text, and then closes the file.</summary>
        /// <param name="self">The storage containing the file.</param>
        /// <param name="id">The identifier of the file to read.</param>
        /// <returns>A string containing all text in the file.</returns>
        /// <exception cref="FileNotFoundException">The specified file was not found.</exception>
        public static string ReadAllText<TFileId>(this ILiteStorage<TFileId> self, TFileId id)
        {
            return ReadAllText(self, id, Utf8NoPreamble);
        }

        /// <summary>Opens a text file, reads all its text using the specified encoding, and then closes the file.</summary>
        /// <param name="self">The storage containing the file.</param>
        /// <param name="id">The identifier of the file to read.</param>
        /// <param name="encoding">The encoding applied to the file content.</param>
        /// <returns>A string containing all text in the file.</returns>
        /// <exception cref="FileNotFoundException">The specified file was not found.</exception>
        public static string ReadAllText<TFileId>(this ILiteStorage<TFileId> self, TFileId id, Encoding encoding)
        {
            if (!self.Exists(id))
            {
                throw new FileNotFoundException("The file specified in id was not found.", Convert.ToString(id));
            }

            using (var stream = self.OpenRead(id))
            using (var reader = new StreamReader(stream, encoding))
            {
                return reader.ReadToEnd();
            }
        }

        /// <summary>Opens a binary file, reads all its bytes, and then closes the file.</summary>
        /// <param name="self">The storage containing the file.</param>
        /// <param name="id">The identifier of the file to read.</param>
        /// <returns>A byte array containing the file content.</returns>
        /// <exception cref="FileNotFoundException">The specified file was not found.</exception>
        public static byte[] ReadAllBytes<TFileId>(this ILiteStorage<TFileId> self, TFileId id)
        {
            if (!self.Exists(id))
            {
                throw new FileNotFoundException("The file specified in id was not found.", Convert.ToString(id));
            }

            using (var stream = self.OpenRead(id))
            using (var target = new MemoryStream())
            {
                stream.CopyTo(target);
                return target.ToArray();
            }
        }

        /// <summary>Creates a file, writes the specified text, and closes the file. An existing file is overwritten.</summary>
        /// <param name="self">The storage containing the file.</param>
        /// <param name="id">The identifier of the file to write.</param>
        /// <param name="filename">The original name of the file.</param>
        /// <param name="contents">The text to write.</param>
        public static void WriteAllText<TFileId>(this ILiteStorage<TFileId> self, TFileId id, string filename, string contents)
        {
            using (var stream = self.OpenWrite(id, filename))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(contents);
            }
        }

        /// <summary>Creates a file, writes text with the specified encoding, and closes the file. An existing file is overwritten.</summary>
        /// <param name="self">The storage containing the file.</param>
        /// <param name="id">The identifier of the file to write.</param>
        /// <param name="filename">The original name of the file.</param>
        /// <param name="contents">The text to write.</param>
        /// <param name="encoding">The encoding applied to the text.</param>
        public static void WriteAllText<TFileId>(this ILiteStorage<TFileId> self, TFileId id, string filename, string contents, Encoding encoding)
        {
            using (var stream = self.OpenWrite(id, filename))
            using (var writer = new StreamWriter(stream, encoding))
            {
                writer.Write(contents);
            }
        }

        /// <summary>Creates a file, writes the specified bytes, and closes the file. An existing file is overwritten.</summary>
        /// <param name="self">The storage containing the file.</param>
        /// <param name="id">The identifier of the file to write.</param>
        /// <param name="filename">The original name of the file.</param>
        /// <param name="bytes">The bytes to write.</param>
        public static void WriteAllBytes<TFileId>(this ILiteStorage<TFileId> self, TFileId id, string filename, byte[] bytes)
        {
            using (var stream = self.OpenWrite(id, filename))
            {
                stream.Write(bytes, 0, bytes.Length);
            }
        }

        /// <summary>
        /// Atomically appends text using UTF-8 without a byte-order mark. A missing file is created.
        /// Existing bytes and metadata are preserved and a failed append is rolled back.
        /// </summary>
        /// <param name="self">The storage containing the file.</param>
        /// <param name="id">The identifier of the file to append.</param>
        /// <param name="filename">The filename used only when a new file is created.</param>
        /// <param name="contents">The text to append.</param>
        public static void AppendAllText<TFileId>(this ILiteStorage<TFileId> self, TFileId id, string filename, string contents)
        {
            AppendAllText(self, id, filename, contents, Utf8NoPreamble, false);
        }

        /// <summary>
        /// Atomically appends text using the specified encoding. A missing file is created.
        /// Existing bytes and metadata are preserved and a failed append is rolled back.
        /// </summary>
        /// <param name="self">The storage containing the file.</param>
        /// <param name="id">The identifier of the file to append.</param>
        /// <param name="filename">The filename used only when a new file is created.</param>
        /// <param name="contents">The text to append.</param>
        /// <param name="encoding">The encoding applied to the appended text.</param>
        public static void AppendAllText<TFileId>(this ILiteStorage<TFileId> self, TFileId id, string filename, string contents, Encoding encoding)
        {
            AppendAllText(self, id, filename, contents, encoding, true);
        }

        private static void AppendAllText<TFileId>(ILiteStorage<TFileId> self, TFileId id, string filename, string contents, Encoding encoding, bool writePreamble)
        {
            if (encoding == null) throw new ArgumentNullException(nameof(encoding));

            using (var stream = self.OpenAppend(id, filename))
            {
                try
                {
                    if (writePreamble && stream.Length == 0)
                    {
                        var preamble = encoding.GetPreamble();
                        stream.Write(preamble, 0, preamble.Length);
                    }

                    using (var writer = new StreamWriter(stream, new NoPreambleEncoding(encoding), 1024, true))
                    {
                        writer.Write(contents);
                    }
                }
                catch
                {
                    stream.Abort();
                    throw;
                }
            }
        }

        private sealed class NoPreambleEncoding : Encoding
        {
            private readonly Encoding _encoding;

            public NoPreambleEncoding(Encoding encoding)
            {
                _encoding = encoding;
            }

            public override int GetByteCount(char[] chars, int index, int count) => _encoding.GetByteCount(chars, index, count);
            public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex) => _encoding.GetBytes(chars, charIndex, charCount, bytes, byteIndex);
            public override int GetCharCount(byte[] bytes, int index, int count) => _encoding.GetCharCount(bytes, index, count);
            public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex) => _encoding.GetChars(bytes, byteIndex, byteCount, chars, charIndex);
            public override int GetMaxByteCount(int charCount) => _encoding.GetMaxByteCount(charCount);
            public override int GetMaxCharCount(int byteCount) => _encoding.GetMaxCharCount(byteCount);
            public override Decoder GetDecoder() => _encoding.GetDecoder();
            public override Encoder GetEncoder() => _encoding.GetEncoder();
            public override byte[] GetPreamble() => Array.Empty<byte>();
        }
    }
}
