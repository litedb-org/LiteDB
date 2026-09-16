using System;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal partial class FileReaderV8
    {
        /// <summary>
        /// Open data file and log file, read header and collection pages
        /// </summary>
        public void Open()
        {
            try
            {
                var dataFactory = _settings.CreateDataFactory();
                var logFactory = _settings.CreateLogFactory();

                // Floor each source independently so partial tails cannot form a page.
                _maxPageID = (uint)(dataFactory.GetLength() / PAGE_SIZE + logFactory.GetLength() / PAGE_SIZE);

                _dataStream = dataFactory.GetStream(true, false);

                var header = WalIdentity.ReadHeader(_dataStream);
                _ = WalIdentity.Read(header);

                if (logFactory.Exists())
                {
                    _logStream = logFactory.GetStream(false, true);

                    if (WalIdentity.Validate(header, _logStream) != WalIdentity.Replay.Completed)
                        this.LoadIndexMap();
                }

                this.LoadPragmas();

                this.LoadDataPages();

                this.LoadCollections();

                this.LoadIndexes();
            }
            catch (LiteException ex) when (ex.ErrorCode == LiteException.INVALID_WAL)
            {
                this.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                this.HandleError(ex, new PageInfo());
            }
        }

    }
}
