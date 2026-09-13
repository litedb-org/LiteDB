using System;
using System.Collections.Generic;
using System.Text;

namespace LiteDB.Tests.CustomMapper.Types
{
    public class Item
    {
        public Guid Id { get; set; }
        public string MyItemName { get; set; }
    }

    public class ItemCollection : List<Item>, ICollectionClass
    {
        public Guid Id { get; set; }
        public string MyItemCollectionName { get; set; }
    }
}
