using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    public partial class LiteEngine
    {
        /// <summary>
        /// Register all internal system collections avaiable by default
        /// </summary>
        private Dictionary<string, SystemCollection> CreateSystemCollections()
        {
            var collections = new Dictionary<string, SystemCollection>(StringComparer.OrdinalIgnoreCase);
            void Add(string name, Func<IEnumerable<BsonDocument>> factory) => collections[name] = new SystemCollection(name, factory);
            void Register(SystemCollection collection) => collections[collection.Name] = collection;

            Add("$database", () => this.SysDatabase());

            Add("$cols", () => this.SysCols());
            Add("$indexes", () => this.SysIndexes());

            Add("$sequences", () => this.SysSequences());

            Add("$transactions", () => this.SysTransactions());
            Add("$snapshots", () => this.SysSnapshots());
            Add("$open_cursors", () => this.SysOpenCursors());

            Register(new SysFile()); // use single $file(?) for all file formats
            Register(new SysDump(_header, _monitor));
            Register(new SysPageList(_header, _monitor));

            Register(new SysQuery(this));
            return collections;
        }
    }
}