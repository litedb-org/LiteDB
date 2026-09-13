using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using static LiteDB.Constants;

namespace LiteDB
{
    /// <summary>
    /// A simple file helper tool with static methods
    /// </summary>
    internal static class FileHelper
    {
        /// <summary>
        /// Create a temp filename based on original filename - checks if file exists (if exists, append counter number)
        /// </summary>
        public static string GetSuffixFile(string filename, string suffix = "-temp", bool checkIfExists = true)
        {
            var count = 0;
            var temp = Path.Combine(Path.GetDirectoryName(filename), 
                Path.GetFileNameWithoutExtension(filename) + suffix + 
                Path.GetExtension(filename));

            while(checkIfExists && File.Exists(temp))
            {
                temp = Path.Combine(Path.GetDirectoryName(filename),
                    Path.GetFileNameWithoutExtension(filename) + suffix +
                    "-" + (++count) +
                    Path.GetExtension(filename));
            }

            return temp;
        }

        /// <summary>
        /// Get LOG file based on data file
        /// </summary>
        public static string GetLogFile(string filename) => GetSuffixFile(filename, "-log", false);

        /// <summary>
        /// Get TEMP file based on data file
        /// </summary>
        public static string GetTempFile(string filename) => GetSuffixFile(filename, "-tmp", false);

        /// <summary>
        /// Test if file are used by any process
        /// </summary>
        public static bool IsFileLocked(string filename)
        {
            FileStream stream = null;
            var file = new FileInfo(filename);

            try
            {
                stream = file.Open(FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex)
            {
                return ex.IsLocked();
            }
            finally
            {
                stream?.Dispose();
            }

            //file is not locked
            return false;
        }

        /// <summary>
        /// Try execute some action while has lock exception
        /// </summary>
        public static bool TryExec(int timeout, Action action)
        {
            var timer = DateTime.UtcNow.AddSeconds(timeout);

            do
            {
                try
                {
                    action();
                    return true;
                }
                catch (IOException ex)
                {
                    ex.WaitIfLocked(25);
                }
            }
            while (DateTime.UtcNow < timer);

            return false;
        }

        /// <summary>
        /// Try execute some action while has lock exception. If timeout occurs, throw last exception
        /// </summary>
        public static void Exec(int timeout, Action action)
        {
            var timer = DateTime.UtcNow.AddSeconds(timeout);
            IOException exception;

            do
            {
                try
                {
                    action();
                    return;
                }
                catch (IOException ex)
                {
                    exception = ex;
                    ex.WaitIfLocked(25);
                }
            }
            while (DateTime.UtcNow < timer);

            throw exception;
        }

        /// <summary>
        /// Convert storage unit string "1gb", "10 mb", "80000" to long bytes
        /// </summary>
        public static long ParseFileSize(string size)
        {
            var match = Regex.Match(size, @"^(\d+)\s*([tgmk])?(b|byte|bytes)?$", RegexOptions.IgnoreCase);

            if (!match.Success) return 0;

            var num = Convert.ToInt64(match.Groups[1].Value);

            switch (match.Groups[2].Value.ToLower())
            {
                case "t": return num * 1024L * 1024L * 1024L * 1024L;
                case "g": return num * 1024L * 1024L * 1024L;
                case "m": return num * 1024L * 1024L;
                case "k": return num * 1024L;
                case "": return num;
            }

            return 0;
        }

        /// <summary>
        /// Format a long file length to pretty file unit
        /// </summary>
        public static string FormatFileSize(long byteCount)
        {
            var suf = new[] { "B", "KB", "MB", "GB", "TB" }; //Longs run out around EB
            if (byteCount == 0) return "0" + suf[0];
            var bytes = Math.Abs(byteCount);
            var place = Convert.ToInt64(Math.Floor(Math.Log(bytes, 1024)));
            var num = Math.Round(bytes / Math.Pow(1024, place), 1);
            return (Math.Sign(byteCount) * num).ToString() + suf[place];
        }
    }
}
