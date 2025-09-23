using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace LiteDB
{
    /// <summary><see cref="LiteStorage"/> extension methods that mimic <see cref="System.IO.File"/> methods.</summary>
    public static class LiteStorageExtensions
    {
        /// <summary>Opens a text file, reads all lines of the file, and then closes the file.</summary>
        /// <param name="self">The this pointer to the <see cref="LiteStorage"/> for the extension method.</param>
        /// <param name="id">The identifier of the file to open for reading.</param>
        /// <returns>A string containing all lines of the file.</returns>
        /// <exception cref="FileNotFoundException">The file specified in id was not found.</exception>
        public static string ReadAllText(this LiteStorage self, string id)
        {
            if (!self.Exists(id)) throw new FileNotFoundException("The file specified in id was not found.", id);
            try
            {
                using (LiteFileStream stream = self.OpenRead(id))
                using (StreamReader reader = new StreamReader(stream))
                {
                    return reader.ReadToEnd();
                }
            }
            catch (Exception) { throw; }
        }

        /// <summary>
        /// Opens a text file, reads all lines of the file with the specified encoding, and then closes the file.
        /// </summary>
        /// <param name="self">The this pointer to the <see cref="LiteStorage" /> for the extension method.</param>
        /// <param name="id">The identifier of the file to open for reading.</param>
        /// <param name="encoding">The encoding applied to the contents of the file.</param>
        /// <returns>
        /// A string containing all lines of the file.
        /// </returns>
        /// <exception cref="FileNotFoundException">The file specified in id was not found.</exception>
        public static string ReadAllText(this LiteStorage self, string id, Encoding encoding)
        {
            if (!self.Exists(id)) throw new FileNotFoundException("The file specified in id was not found.", id);
            try
            {
                using (LiteFileStream stream = self.OpenRead(id))
                using (StreamReader reader = new StreamReader(stream, encoding))
                {
                    return reader.ReadToEnd();
                }
            }
            catch (Exception) { throw; }
        }

        /// <summary>Opens a binary file, reads the contents of the file into a byte array, and then closes the file.</summary>
        /// <param name="self">The this pointer to the <see cref="LiteStorage"/> for the extension method.</param>
        /// <param name="id">The identifier of the file to open for reading.</param>
        /// <returns>A byte array containing the contents of the file.</returns>
        /// <exception cref="FileNotFoundException">The file specified in id was not found.</exception>
        public static byte[] ReadAllBytes(this LiteStorage self, string id)
        {
            if (!self.Exists(id)) throw new FileNotFoundException("The file specified in id was not found.", id);
            try
            {
                using (LiteFileStream stream = self.OpenRead(id))
                using (MemoryStream mstream = new MemoryStream())
                {
                    stream.CopyTo(mstream);
                    return mstream.ToArray();
                }
            }
            catch (Exception) { throw; }
        }

        /// <summary>Creates a new file, writes the specified string to the file, and then closes the file. If the target file already exists, it is overwritten.</summary>
        /// <param name="self">The this pointer to the <see cref="LiteStorage"/> for the extension method.</param>
        /// <param name="id">The identifier of the file to write to.</param>
        /// <param name="filename">The original name of the file.</param>
        /// <param name="contents">The string to write to the file.</param>
        public static void WriteAllText(this LiteStorage self, string id, string filename, string contents)
        {
            try
            {
                using (LiteFileStream stream = self.OpenWrite(id, filename))
                using (StreamWriter writer = new StreamWriter(stream))
                {
                    writer.Write(contents);
                }
            }
            catch (Exception) { throw; }
        }

        /// <summary>Creates a new file, writes the specified string to the file using the specified encoding, and then closes the file. If the target file already exists, it is overwritten.</summary>
        /// <param name="self">The this pointer to the <see cref="LiteStorage"/> for the extension method.</param>
        /// <param name="id">The identifier of the file to write to.</param>
        /// <param name="filename">The original name of the file.</param>
        /// <param name="contents">The string to write to the file.</param>
        /// <param name="encoding">The encoding to apply to the string.</param>
        public static void WriteAllText(this LiteStorage self, string id, string filename, string contents, Encoding encoding)
        {
            try
            {
                using (LiteFileStream stream = self.OpenWrite(id, filename))
                using (StreamWriter writer = new StreamWriter(stream, encoding))
                {
                    writer.Write(contents);
                }
            }
            catch (Exception) { throw; }
        }

        /// <summary>Creates a new file, writes the specified byte array to the file, and then closes the file. If the target file already exists, it is overwritten.</summary>
        /// <param name="self">The this pointer to the <see cref="LiteStorage"/> for the extension method.</param>
        /// <param name="id">The identifier of the file to write to.</param>
        /// <param name="filename">The original name of the file.</param>
        /// <param name="bytes">The bytes to write to the file.</param>
        public static void WriteAllBytes(this LiteStorage self, string id, string filename, byte[] bytes)
        {
            try
            {
                using (LiteFileStream stream = self.OpenWrite(id, filename))
                {
                    stream.Write(bytes, 0, bytes.Length);
                }
            }
            catch (Exception) { throw; }
        }

        /// <summary>Opens a file, appends the specified string to the file, and then closes the file. If the file does not exist, this method creates a file, writes the specified string to the file, then closes the file.</summary>
        /// <param name="self">The this pointer to the <see cref="LiteStorage"/> for the extension method.</param>
        /// <param name="id">The identifier of the file to append the specified string to.</param>
        /// <param name="filename">The original name of the file.</param>
        /// <param name="contents">The string to append to the file.</param>
        public static void AppendAllText(this LiteStorage self, string id, string filename, string contents)
        {
            try
            {
                if (!self.Exists(id))
                    WriteAllText(self, id, filename, contents);
                else
                    WriteAllText(self, id, filename, ReadAllText(self, id) + contents);
            }
            catch (Exception) { throw; }
        }

        /// <summary>Opens a file, appends the specified string to the file, and then closes the file. If the file does not exist, this method creates a file, writes the specified string to the file, then closes the file.</summary>
        /// <param name="self">The this pointer to the <see cref="LiteStorage"/> for the extension method.</param>
        /// <param name="id">The identifier of the file to append the specified string to.</param>
        /// <param name="filename">The original name of the file.</param>
        /// <param name="contents">The string to append to the file.</param>
        /// <param name="encoding">The character encoding to use.</param>
        public static void AppendAllText(this LiteStorage self, string id, string filename, string contents, Encoding encoding)
        {
            try
            {
                if (!self.Exists(id))
                    WriteAllText(self, id, filename, contents, encoding);
                else
                    WriteAllText(self, id, filename, ReadAllText(self, id, encoding) + contents, encoding);
            }
            catch (Exception) { throw; }
        }
    }
}