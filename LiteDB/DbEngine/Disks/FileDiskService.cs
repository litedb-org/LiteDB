﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace LiteDB
{
    internal class FileDiskService : IDiskService
    {
        /// <summary>
        /// Position on disk to write a mark to know when journal is finish and valid (byte 19 is free header area)
        /// </summary>
        private const int JOURNAL_FINISH_POSITION = 19;

        /// <summary>
        /// Position, on page, about page type
        /// </summary>
        private const int PAGE_TYPE = 4;

        private FileStream _stream;
        private string _filename;
        private long _lockLength;

        private FileStream _journal;
        private string _journalFilename;
        private bool _journalEnabled;

        private Logger _log;
        private TimeSpan _timeout;
        private bool _readonly;
        private string _password;

        public FileDiskService(string connectionString, Logger log)
        {
            var str = new ConnectionString(connectionString);

            _filename = str.GetValue<string>("filename", "");
            var journalEnabled = str.GetValue<bool>("journal", true);
            _timeout = str.GetValue<TimeSpan>("timeout", new TimeSpan(0, 1, 0));
            _readonly = str.GetValue<bool>("readonly", false);
            _password = str.GetValue<string>("password", null);
            var level = str.GetValue<byte?>("log", null);

            if (string.IsNullOrWhiteSpace(_filename)) throw new ArgumentNullException("filename");

            // setup log + log-level
            _log = log;
            if(level.HasValue) _log.Level = level.Value;

            _journalEnabled = _readonly ? false : journalEnabled; // readonly? no journal
            _journalFilename = Path.Combine(Path.GetDirectoryName(_filename),
                Path.GetFileNameWithoutExtension(_filename) + "-journal" +
                Path.GetExtension(_filename));
        }

        /// <summary>
        /// Open datafile - returns true if new
        /// </summary>
        public bool Initialize()
        {
            _log.Write(Logger.DISK, "open datafile '{0}', page size {1}", Path.GetFileName(_filename), BasePage.PAGE_SIZE);

            // open data file (r/w or r)
            _stream = new FileStream(_filename, 
                _readonly ? FileMode.Open : FileMode.OpenOrCreate, 
                _readonly ? FileAccess.Read : FileAccess.ReadWrite, 
                _readonly ? FileShare.Read : FileShare.ReadWrite, 
                BasePage.PAGE_SIZE);

            if(_stream.Length == 0)
            {
                _log.Write(Logger.DISK, "initialize new datafile");

                return true;
            }
            else
            {
                this.TryRecovery();
                return false;
            }
        }

        #region Lock/Unlock

        /// <summary>
        /// Lock datafile agains other process read/write
        /// </summary>
        public void Lock()
        {
            this.TryExec(() => {
                _lockLength = _stream.Length;
                _log.Write(Logger.DISK, "lock datafile");
                _stream.Lock(0, _lockLength);
            });
        }

        /// <summary>
        /// Release lock
        /// </summary>
        public void Unlock()
        {
            _log.Write(Logger.DISK, "unlock datafile");
            _stream.Unlock(0, _lockLength);
        }

        #endregion

        #region Read/Write

        /// <summary>
        /// Read first 2 bytes from datafile - contains changeID (avoid to read all header page)
        /// </summary>
        public ushort GetChangeID()
        {
            var bytes = new byte[2];
            _stream.Seek(HeaderPage.CHANGE_ID_POSITION, SeekOrigin.Begin);
            _stream.Read(bytes, 0, 2);
            return BitConverter.ToUInt16(bytes, 0);
        }

        /// <summary>
        /// Read page bytes from disk
        /// </summary>
        public byte[] ReadPage(uint pageID)
        {
            var buffer = new byte[BasePage.PAGE_SIZE];
            var position = (long)pageID * (long)BasePage.PAGE_SIZE;

            this.TryExec(() => 
            {
                // position cursor
                if (_stream.Position != position)
                {
                    _stream.Seek(position, SeekOrigin.Begin);
                }

                // read bytes from data file
                _stream.Read(buffer, 0, BasePage.PAGE_SIZE);
            });

            _log.Write(Logger.DISK, "read page #{0:0000} :: {1}", pageID, (PageType)buffer[PAGE_TYPE]);

            return buffer;
        }

        /// <summary>
        /// Persist single page bytes to disk
        /// </summary>
        public void WritePage(uint pageID, byte[] buffer)
        {
            var position = (long)pageID * (long)BasePage.PAGE_SIZE;

            _log.Write(Logger.DISK, "write page #{0:0000} :: {1}", pageID, (PageType)buffer[PAGE_TYPE]);

            // position cursor
            if (_stream.Position != position)
            {
                _stream.Seek(position, SeekOrigin.Begin);
            }

            _stream.Write(buffer, 0, BasePage.PAGE_SIZE);
        }

        #endregion

        #region Journal file

        public void WriteJournal(uint pageID, byte[] buffer)
        {
            if(_journalEnabled == false) return;

            // open journal file if not used yet
            if(_journal == null)
            {
                // open journal file in EXCLUSIVE mode
                this.TryExec(() =>
                {
                    _log.Write(Logger.JOURNAL, "create journal file");

                    _journal = new FileStream(_journalFilename, FileMode.Create, FileAccess.ReadWrite, FileShare.None, BasePage.PAGE_SIZE);
                });
            }

            _log.Write(Logger.JOURNAL, "write page #{0:0000} :: {1}", pageID, (PageType)buffer[PAGE_TYPE]);

            // just write original bytes in order that are changed
            _journal.Write(buffer, 0, BasePage.PAGE_SIZE);
        }

        public void CommitJournal(long fileSize)
        {
            if (_journalEnabled == false) return;

            if(_journal != null)
            {
                _log.Write(Logger.JOURNAL, "commit journal file");

                // write a mark (byte 1) to know when journal is finish
                // after that, if found a non-exclusive-open journal file, must be recovery
                _journal.WriteByte(JOURNAL_FINISH_POSITION, 1);

                // flush all journal file data to disk
                _journal.Flush();
            }

            // fileSize parameter tell me final size of data file - helpful to extend first datafile
            _stream.SetLength(fileSize);
        }

        public void DeleteJournal()
        {
            if (_journalEnabled == false) return;

            if(_journal != null)
            {
                _log.Write(Logger.JOURNAL, "delete journal file");

                // close journal stream and delete file
                _journal.Dispose();
                _journal = null;

                // remove journal file
                File.Delete(_journalFilename);
            }
        }

        public void Dispose()
        {
            if(_stream != null)
            {
                _stream.Dispose();
            }
        }

        #endregion

        #region Recovery datafile

        private void TryRecovery()
        {
            // if I can open journal file, test FINISH_POSITION. If no journal, do not call action()
            this.OpenExclusiveFile(_journalFilename, (journal) =>
            {
                _log.Write(Logger.RECOVERY, "journal file detected");

                var finish = journal.ReadByte(JOURNAL_FINISH_POSITION);

                // test if journal was finish
                if(finish == 1)
                {
                    this.Recovery(journal);
                }
                else
                {
                    _log.Write(Logger.RECOVERY, "journal file are not commited, no recovery");
                }

                // close stream for delete file
                journal.Close();

                File.Delete(_journalFilename);

                _log.Write(Logger.RECOVERY, "recovery finish");
            });
        }

        private void Recovery(FileStream journal)
        {
            var fileSize = _stream.Length;
            var buffer = new byte[BasePage.PAGE_SIZE];

            journal.Seek(0, SeekOrigin.Begin);

            while (journal.Position < journal.Length)
            {
                // read page bytes from journal file
                journal.Read(buffer, 0, BasePage.PAGE_SIZE);

                // read pageID (first 4 bytes)
                var pageID = BitConverter.ToUInt32(buffer, 0);

                _log.Write(Logger.RECOVERY, "recover page #{0:0000}", pageID);

                // if header, read all byte (to get original filesize)
                if (pageID == 0)
                {
                    var header = (HeaderPage)BasePage.ReadPage(buffer);

                    fileSize = (header.LastPageID + 1) * BasePage.PAGE_SIZE;
                }

                // write in stream
                _stream.Seek(pageID * BasePage.PAGE_SIZE, SeekOrigin.Begin);
                _stream.Write(buffer, 0, BasePage.PAGE_SIZE);
            }

            _log.Write(Logger.RECOVERY, "resize datafile to {0} bytes", fileSize);

            // redim filesize if grow more than original before rollback
            _stream.SetLength(fileSize);
        }

        #endregion

        #region Utils

        /// <summary>
        /// Try run an operation over datafile - keep tring if locked
        /// </summary>
        private void TryExec(Action action)
        {
            var timer = DateTime.Now.Add(_timeout);

            while (DateTime.Now < timer)
            {
                try
                {
                    action();
                    return;
                }
                catch (UnauthorizedAccessException)
                {
                    Thread.Sleep(250);
                }
                catch (IOException ex)
                {
                    ex.WaitIfLocked(250);
                }
            }

            _log.Write(Logger.ERROR, "timeout disk access after {0}", _timeout);

            throw LiteException.LockTimeout(_timeout);
        }

        private void OpenExclusiveFile(string filename, Action<FileStream> success)
        {
            // check if is using by another process, if not, call fn passing stream opened
            try
            {
                using (var stream = File.Open(filename, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    success(stream);
                }
            }
            catch (FileNotFoundException)
            {
                // do nothing - no journal file, no recovery
            }
            catch (IOException ex)
            {
                ex.WaitIfLocked(0);
            }
        }

        #endregion
    }
}
