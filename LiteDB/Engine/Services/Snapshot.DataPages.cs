using System.Collections.Generic;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal partial class Snapshot
    {
        /// <summary>
        /// Zwróć wszystkie strony danych kolekcji iterując przez 5 slotów listy wolnych stron
        /// (0..4). Obejmuje także "pełne" strony (slot 4).
        /// </summary>
        public IEnumerable<DataPage> EnumerateDataPages()
        {
            ENSURE(!_disposed, "the snapshot is disposed");

            if (_collectionPage == null) yield break;

            var counter = 0u;

            for (int slot = 0; slot < PAGE_FREE_LIST_SLOTS; slot++)
            {
                var next = _collectionPage.FreeDataPageList[slot];

                while (next != uint.MaxValue)
                {
                    var page = this.GetPage<DataPage>(next);

                    next = page.NextPageID;
                    yield return page;

                    ENSURE(counter++ < _disk.MAX_ITEMS_COUNT, "Detected loop in EnumerateDataPages({0})", _collectionName);
                }
            }
        }
    }
}
