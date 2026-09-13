using LiteDB.Engine;

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

[assembly: InternalsVisibleTo("LiteDB.Tests, PublicKey=002400000480000094000000060200000024000052534131000400000100010029e66990e22110ce40a7197e37f8f82df3332c399e696df7f27d09e14ee590ac2dda735d4777fe554c427540bde93b14d3d26c04731c963383dcaa18859c8cbcd4a1a9c394d1204f474c2ab6f23a2eaadf81eb8a7a3d3cc73658868b0302163b92a2614ca050ab703be33c3e1d76f55b11f4f87cb73558f3aa69c1ce726d9ee8")]
#if DEBUG
[assembly: InternalsVisibleTo("ConsoleApp1")]
#endif

namespace LiteDB
{
    using System.Globalization;

    /// <summary>
    /// Class with all constants used in LiteDB + Debbuger HELPER
    /// </summary>
    internal class Constants
    {
        /// <summary>
        /// The size of each page in disk - use 8192 as all major databases
        /// </summary>
        public const int PAGE_SIZE = 8192;

        /// <summary>
        /// Header page size
        /// </summary>
        public const int PAGE_HEADER_SIZE = 32;

        /// <summary>
        /// Bytes used in encryption salt
        /// </summary>
        public const int ENCRYPTION_SALT_SIZE = 16;

        /// <summary>
        /// Define ShareCounter buffer as writable
        /// </summary>
        public static int BUFFER_WRITABLE = -1;

        /// <summary>
        /// Define index name max length
        /// </summary>
        public static int INDEX_NAME_MAX_LENGTH = 32;

        /// <summary>
        /// Max level used on skip list (index).
        /// </summary>
        public const int MAX_LEVEL_LENGTH = 32;

        /// <summary>
        /// Max size of a index entry - usde for string, binary, array and documents. Need fit in 1 byte length
        /// </summary>
        public const int MAX_INDEX_KEY_LENGTH = 1023;

        /// <summary>
        /// Get max length of 1 single index node
        /// </summary>
        public const int MAX_INDEX_LENGTH = 1400;

        /// <summary>
        /// Get how many slots collection pages will have for free list page (data/index)
        /// </summary>
        public const int PAGE_FREE_LIST_SLOTS = 5;

        /// <summary>
        /// Document limit size - 2048 data pages limit (about 16Mb - same size as MongoDB)
        /// Using 2047 because first/last page can contain less than 8150 bytes.
        /// </summary>
        public const int MAX_DOCUMENT_SIZE = 2047 * DataService.MAX_DATA_BYTES_PER_PAGE;

        /// <summary>
        /// Define how many transactions can be open simultaneously
        /// </summary>
        public const int MAX_OPEN_TRANSACTIONS = 100;

        /// <summary>
        /// Define how many pages all transaction will consume, in memory, before persist in disk. This amount are shared across all open transactions
        /// 100,000 ~= 1Gb memory
        /// </summary>
        public const int MAX_TRANSACTION_SIZE = 100_000; // 100_000 (default) - 1000 (for tests)

        /// <summary>
        /// Size, in PAGES, for each buffer array (used in MemoryStore)
        /// It's an array to increase after each extend - limited in highest value
        /// Each byte array will be created with this size * PAGE_SIZE
        /// Use minimal 12 to allocate at least 85Kb per segment (will use LOH)
        /// </summary>
        public static int[] MEMORY_SEGMENT_SIZES = new int[] { 12, 50, 100, 500, 1000 }; // 8Mb per extend

        /// <summary>
        /// Define how many documents will be keep in memory until clear cache and remove support to orderby/groupby
        /// </summary>
        public const int VIRTUAL_INDEX_MAX_CACHE = 2000;

        /// <summary>
        /// Define how many bytes each merge sort container will be created
        /// </summary>
        public const int CONTAINER_SORT_SIZE = 100 * PAGE_SIZE;

        /// <summary>
        /// Initial seed for Random
        /// </summary>
#if DEBUG
        public const int RANDOMIZER_SEED = 3131;
#else
        public const int RANDOMIZER_SEED = 0;
#endif

        /// <summary>
        /// Log a message using Debug.WriteLine
        /// </summary>
        [DebuggerHidden]
        [Conditional("DEBUG")]
        public static void LOG(string message, string category)
        {
            //Debug.WriteLine is too slow in multi-threads
            //var threadID = Environment.CurrentManagedThreadId;
            //Debug.WriteLine(message, threadID + "|" + category);
        }

        /// <summary>
        /// Log a message using Debug.WriteLine only if conditional = true
        /// </summary>
        [DebuggerHidden]
        [Conditional("DEBUG")]
        public static void LOG(bool conditional, string message, string category)
        {
            if (conditional) LOG(message, category);
        }

        /// <summary>
        /// Ensure condition is true, otherwise throw exception (check contract)
        /// </summary>
        [DebuggerHidden]
        public static void ENSURE(bool conditional, string message = null)
        {
            if (conditional == false)
            {
                if (Debugger.IsAttached)
                {
                    Debugger.Break();
                }

                throw LiteException.InvalidDatafileState(message);
            }
        }

        [DebuggerHidden]
        public static void ENSURE(bool conditional, string format, params object[] args)
        {
            if (conditional == false)
            {
                if (Debugger.IsAttached)
                {
                    Debugger.Break();
                }

                var message = string.Format(CultureInfo.InvariantCulture, format, args);

                throw LiteException.InvalidDatafileState(format);
            }
        }

        /// <summary>
        /// If ifTest are true, ensure condition is true, otherwise throw ensure exception (check contract)
        /// </summary>
        [DebuggerHidden]
        public static void ENSURE(bool ifTest, bool conditional, string message = null)
        {
            if (ifTest && conditional == false)
            {
                if (Debugger.IsAttached)
                {
                    Debugger.Break();
                }

                throw LiteException.InvalidDatafileState(message);
            }
        }

        /// <summary>
        /// Ensure condition is true, otherwise throw exception (runs only in DEBUG mode)
        /// </summary>
        [DebuggerHidden]
        [Conditional("DEBUG")]
        public static void DEBUG(bool conditional, string message = null)
        {
            if (conditional == false)
            {
                if (Debugger.IsAttached)
                {
                    Debugger.Break();
                }

                throw LiteException.InvalidDatafileState(message);
            }
        }
    }
}