using System;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    public partial class AesStream
    {
        /// <summary>
        /// Recovery records are WAL padding, even if their ciphertext is torn before sync.
        /// Keep the raw blank-page sentinel (16 zero bytes) instead of encrypting that
        /// prefix. The remaining blocks, including the header image, stay encrypted.
        /// Ordinary readers then skip even an incomplete record; only the journal
        /// reader decrypts its payload and verifies its checksum.
        /// </summary>
        internal void WriteHeaderPromotionPage(byte[] page)
        {
            var ciphertext = _bufferPool.Rent(PAGE_SIZE);
            try
            {
                _encryptor.TransformBlock(page, 0, PAGE_SIZE, ciphertext, 0);
                Array.Clear(ciphertext, 0, 16);
                _stream.Write(ciphertext, 0, PAGE_SIZE);
            }
            finally { _bufferPool.Return(ciphertext, true); }
        }

        internal void ReadHeaderPromotionPage(byte[] page)
        {
            _reader.ReadRequired(page, 0, PAGE_SIZE);
            if (IsBlank(page, 0)) Array.Clear(page, 0, 16);
        }
    }
}
