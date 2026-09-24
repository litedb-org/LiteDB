using System;
using System.IO;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Resume an empty encrypted WAL's preamble after the data file authenticated
    /// the password. Never apply this policy to a data file or existing WAL pages.
    /// </summary>
    internal static class EncryptedLogPreamble
    {
        private const int SaltEnd = 1 + ENCRYPTION_SALT_SIZE;
        private const int CheckEnd = AesPreamble.CHECK_START + AesPreamble.CHECK_SIZE;

        internal static Stream Open(string password, Stream stream)
        {
            try
            {
                if (stream.Length >= PAGE_SIZE) return new AesStream(password, stream, allowRecovery: false);

                var bytes = new byte[(int)stream.Length];
                stream.Position = 0;
                stream.ReadRequired(bytes, 0, bytes.Length);
                if (bytes.Length != 0 && bytes[0] != 1) throw LiteException.InvalidDatabase();
                for (var i = SaltEnd; i < bytes.Length; i++)
                    if ((i < AesPreamble.CHECK_START || i >= CheckEnd) && bytes[i] != 0)
                        throw LiteException.InvalidDatabase();

                // No ciphertext payload exists below the hidden-page boundary.
                // Retain every existing salt byte and generate only its suffix.
                var salt = AesStream.NewSalt();
                if (bytes.Length > 1) Buffer.BlockCopy(bytes, 1, salt, 0, Math.Min(salt.Length, bytes.Length - 1));
                byte[] expected;
                using (var template = new MemoryStream())
                {
                    template.WriteByte(1);
                    template.Write(salt, 0, salt.Length);
                    using (var encrypted = new AesStream(password, new NonClosingStream(template), allowRecovery: false)) { }
                    expected = template.ToArray();
                }

                var checkPrefix = 0;
                while (AesPreamble.CHECK_START + checkPrefix < Math.Min(CheckEnd, bytes.Length) &&
                    bytes[AesPreamble.CHECK_START + checkPrefix] == expected[AesPreamble.CHECK_START + checkPrefix])
                    checkPrefix++;
                // A torn append leaves an expected ciphertext prefix, possibly
                // followed by zero extension. Foreign ciphertext fails closed.
                for (var i = AesPreamble.CHECK_START + checkPrefix; i < Math.Min(CheckEnd, bytes.Length); i++)
                    if (bytes[i] != 0) throw LiteException.InvalidPassword();

                if (!stream.CanWrite)
                {
                    stream.Dispose();
                    return new MemoryStream(Array.Empty<byte>(), writable: false);
                }

                if (bytes.Length < SaltEnd)
                {
                    stream.Position = bytes.Length;
                    stream.Write(expected, bytes.Length, SaltEnd - bytes.Length);
                }
                // A later check write must never outlive the salt that decrypts it.
                stream.FlushToDisk();
                var checkPosition = AesPreamble.CHECK_START + checkPrefix;
                if (checkPosition < CheckEnd)
                {
                    stream.Position = checkPosition;
                    stream.Write(expected, checkPosition, CheckEnd - checkPosition);
                }
                // A page-sized preamble must imply a complete password check.
                stream.FlushToDisk();
                stream.Position = PAGE_SIZE - 1;
                stream.WriteByte(0);
                stream.FlushToDisk();
                return new AesStream(password, stream, allowRecovery: false);
            }
            catch
            {
                try { stream.Dispose(); } catch { }
                throw;
            }
        }
    }
}
