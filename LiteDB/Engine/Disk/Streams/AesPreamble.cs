using System.Buffers;
using System.IO;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Tells an interrupted encrypted creation (marker, salt and nothing but zeros) apart from foreign content.
    /// Completing the former only fills zero bytes; the latter must never be written to.
    /// </summary>
    internal static class AesPreamble
    {
        public const int CHECK_START = 32;
        public const int CHECK_SIZE = 32;

        private const int SALT_END = 1 + ENCRYPTION_SALT_SIZE;
        private const int CHECK_END = CHECK_START + CHECK_SIZE;

        /// <summary>
        /// Reject a short stream that does not even hold the encryption marker and a full salt.
        /// </summary>
        public static void EnsureMarker(Stream stream)
        {
            var length = stream.Length;

            if (length == 0 || length >= PAGE_SIZE) return;

            stream.Position = 0;

            if (length < SALT_END || stream.ReadByte() != 1) throw LiteException.InvalidDatabase();
        }

        /// <summary>
        /// True when the password check block was never written and no other content exists.
        /// </summary>
        public static bool IsInterruptedCreation(Stream stream)
        {
            if (IsBlank(stream, SALT_END, stream.Length))
            {
                if (!stream.CanWrite) throw LiteException.InvalidDatabase();

                return true;
            }

            // a missing or zero check block in front of other content is not a LiteDB preamble
            if (stream.Length < CHECK_END || IsBlank(stream, CHECK_START, CHECK_END))
            {
                throw LiteException.InvalidDatabase();
            }

            return false;
        }

        /// <summary>
        /// Extend a verified preamble that was cut before its hidden page was complete.
        /// </summary>
        public static void CompletePage(Stream stream)
        {
            if (stream.Length >= PAGE_SIZE) return;

            if (!stream.CanWrite || !IsBlank(stream, CHECK_END, stream.Length)) throw LiteException.InvalidDatabase();

            stream.Position = PAGE_SIZE - 1;
            stream.WriteByte(0);
        }

        private static bool IsBlank(Stream stream, int from, long to)
        {
            if (to > PAGE_SIZE) return false;

            var count = (int)to - from;
            var buffer = ArrayPool<byte>.Shared.Rent(PAGE_SIZE);

            try
            {
                stream.Position = from;
                stream.ReadRequired(buffer, 0, count);

                for (var i = 0; i < count; i++)
                {
                    if (buffer[i] != 0) return false;
                }

                return true;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, true);
            }
        }
    }
}
