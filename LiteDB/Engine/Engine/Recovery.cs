using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using static LiteDB.Constants;

namespace LiteDB.Engine
{
    public partial class LiteEngine
    {
        internal void EnsureFreeEmptyPageListIsHealthy()
        {
            if (System.Threading.Interlocked.Exchange(ref _deferFreeEmptyPageListValidation, 0) == 0)
            {
                return;
            }

            this.HealCorruptedFreeEmptyPageList();
        }

        private void HealCorruptedFreeEmptyPageList()
        {
            if (_header.FreeEmptyPageList == uint.MaxValue)
            {
                return;
            }

            using var reader = _disk.GetReader();

            var current = _header.FreeEmptyPageList;
            var visited = new HashSet<uint>();

            while (current != uint.MaxValue)
            {
                if (current > _header.LastPageID || visited.Add(current) == false)
                {
                    this.RepairFreeEmptyPageList(current, null);
                    return;
                }

                BasePage page = null;

                try
                {
                    page = this.ReadLatestPage(current, reader);

                    if (page.PageType != PageType.Empty)
                    {
                        this.RepairFreeEmptyPageList(current, page.PageType);
                        return;
                    }

                    current = page.NextPageID;
                }
                catch
                {
                    this.RepairFreeEmptyPageList(current, page?.PageType);
                    return;
                }
                finally
                {
                    page?.Buffer.Release();
                }
            }
        }

        private BasePage ReadLatestPage(uint pageID, DiskReader reader)
        {
            var position = _walIndex.GetPageIndex(pageID, int.MaxValue, out _);

            if (position != long.MaxValue)
            {
                return new BasePage(reader.ReadPage(position, false, FileOrigin.Log));
            }

            return new BasePage(reader.ReadPage(BasePage.GetPagePosition(pageID), false, FileOrigin.Data));
        }

        private void RepairFreeEmptyPageList(uint pageID, PageType? pageType)
        {
            LOG(
                pageType.HasValue
                    ? $"detected legacy corruption in free empty page list at page {pageID} ({pageType.Value}); resetting header free list"
                    : $"detected legacy corruption in free empty page list near page {pageID}; resetting header free list",
                "RECOVERY");

            lock (_header)
            {
                var savepoint = _header.Savepoint();

                try
                {
                    _header.FreeEmptyPageList = uint.MaxValue;
                    _header.TransactionID = uint.MaxValue;
                    _header.IsConfirmed = false;
                    _header.UpdateBuffer();

                    if (_settings.ReadOnly == false)
                    {
                        this.PersistRecoveredHeader();
                    }
                }
                catch
                {
                    _header.Restore(savepoint);
                    throw;
                }
            }
        }

        private void PersistRecoveredHeader()
        {
            var transactionID = _walIndex.NextTransactionID();

            _header.TransactionID = transactionID;
            _header.IsConfirmed = true;

            var buffer = _header.UpdateBuffer();
            var clone = _disk.NewPage();

            Buffer.BlockCopy(buffer.Array, buffer.Offset, clone.Array, clone.Offset, clone.Count);

            _disk.WriteLogDisk(new[] { clone });
            _walIndex.ConfirmTransaction(transactionID, new[] { new PagePosition(0, clone.Position) });

            _header.TransactionID = uint.MaxValue;
            _header.IsConfirmed = false;
            _header.UpdateBuffer();
        }

        /// <summary>
        /// Recovery datafile using a rebuild process. Run only on "Open" database
        /// </summary>
        private void Recovery(Collation collation)
        {
            // run build service
            var rebuilder = new RebuildService(_settings);
            var options = new RebuildOptions
            {
                Collation = collation,
                Password = _settings.Password,
                IncludeErrorReport = true
            };

            // run rebuild process
            rebuilder.Rebuild(options);
        }
    }
}
