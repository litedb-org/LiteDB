﻿using LiteDB.Shell;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace LiteDB
{
    internal partial class DbEngine : IDisposable
    {
        /// <summary>
        /// Returns all collection inside datafile
        /// </summary>
        public IEnumerable<string> GetCollectionNames()
        {
            _transaction.AvoidDirtyRead();

            return _collections.GetAll().Select(x => x.CollectionName);
        }

        /// <summary>
        /// Drop collection including all documents, indexes and extended pages
        /// </summary>
        public bool DropCollection(string colName)
        {
            return this.Transaction<bool>(colName, false, (col) =>
            {
                if(col == null) return false;

                _log.Write(Logger.COMMAND, "drop collection {0}", colName);

                _collections.Drop(col);

                return true;
            });
        }

        /// <summary>
        /// Rename a collection
        /// </summary>
        public bool RenameCollection(string colName, string newName)
        {
            return this.Transaction<bool>(colName, false, (col) =>
            {
                if(col == null) return false;

                _log.Write(Logger.COMMAND, "rename collection '{0}' -> '{1}'", colName, newName);

                // change collection name and save
                col.CollectionName = newName;

                _pager.SetDirty(col);

                return true;
            });
        }
    }
}
