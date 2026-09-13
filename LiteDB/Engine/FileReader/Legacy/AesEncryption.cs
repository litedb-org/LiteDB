using System;
using System.Security.Cryptography;
using System.IO;
using System.Text;
using static LiteDB.Constants;

namespace LiteDB
{
    /// <summary>
    /// Encryption AES wrapper to encrypt data pages
    /// </summary>
    internal class AesEncryption : IDisposable
    {
        private Aes _aes;

        public AesEncryption(string password, byte[] salt)
        {
            _aes = Aes.Create();
            _aes.Padding = PaddingMode.Zeros;

            var pdb = new Rfc2898DeriveBytes(password, salt);

            using (pdb as IDisposable)
            {
                _aes.Key = pdb.GetBytes(32);
                _aes.IV = pdb.GetBytes(16);
            }
        }

        /// <summary>
        /// Encrypt byte array returning new encrypted byte array with same length of original array (PAGE_SIZE)
        /// </summary>
        public byte[] Encrypt(byte[] bytes)
        {
            using (var encryptor = _aes.CreateEncryptor())
            using (var stream = new MemoryStream())
            using (var crypto = new CryptoStream(stream, encryptor, CryptoStreamMode.Write))
            {
                crypto.Write(bytes, 0, bytes.Length);
                crypto.FlushFinalBlock();
                stream.Position = 0;
                var encrypted = new byte[stream.Length];
                stream.Read(encrypted, 0, encrypted.Length);

                return encrypted;
            }
        }

        /// <summary>
        /// Decrypt and byte array returning a new byte array
        /// </summary>
        public byte[] Decrypt(byte[] encryptedValue, int offset = 0, int count = -1)
        {
            using (var decryptor = _aes.CreateDecryptor())
            using (var stream = new MemoryStream())
            using (var crypto = new CryptoStream(stream, decryptor, CryptoStreamMode.Write))
            {
                crypto.Write(encryptedValue, offset, count == -1 ? encryptedValue.Length : count);
                crypto.FlushFinalBlock();
                stream.Position = 0;
                var decryptedBytes = new byte[stream.Length];
                stream.Read(decryptedBytes, 0, decryptedBytes.Length);

                return decryptedBytes;
            }
        }

        /// <summary>
        /// Hash a password using SHA1 just to verify password
        /// </summary>
        public static byte[] HashSHA1(string password)
        {
            var sha = SHA1.Create();
            var shaBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(password));
            return shaBytes;
        }

        /// <summary>
        /// Generate a salt key that will be stored inside first page database
        /// </summary>
        /// <returns></returns>
        public static byte[] Salt(int maxLength = 16)
        {
            var salt = new byte[maxLength];
            {
                var rng = RandomNumberGenerator.Create();
                using (rng as IDisposable)
                    rng.GetBytes(salt);
            }
            return salt;
        }

        public void Dispose()
        {
            if (_aes != null)
            {
                _aes.Dispose();
                _aes = null;
            }
        }
    }
}