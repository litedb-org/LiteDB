using System.Collections.Generic;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal sealed class VectorIndexPage : BasePage
    {
        public VectorIndexPage(PageBuffer buffer)
            : base(buffer)
        {
            ENSURE(this.PageType == PageType.VectorIndex, "page type must be vector index page");

            if (this.PageType != PageType.VectorIndex) throw LiteException.InvalidPageType(PageType.VectorIndex, this);
        }

        public VectorIndexPage(PageBuffer buffer, uint pageID)
            : base(buffer, pageID, PageType.VectorIndex)
        {
        }

        public VectorIndexNode GetNode(byte index)
        {
            var segment = base.Get(index);

            return new VectorIndexNode(this, index, segment);
        }

        public VectorIndexNode InsertNode(PageAddress dataBlock, float[] vector, int bytesLength)
        {
            var segment = base.Insert((ushort)bytesLength, out var index);

            return new VectorIndexNode(this, index, segment, dataBlock, vector);
        }

        public void DeleteNode(byte index)
        {
            base.Delete(index);
        }

        public IEnumerable<VectorIndexNode> GetNodes()
        {
            foreach (var index in base.GetUsedIndexs())
            {
                yield return this.GetNode(index);
            }
        }

        public static byte FreeListSlot(int freeBytes)
        {
            ENSURE(freeBytes >= 0, "freeBytes must be positive");

            return freeBytes >= MAX_INDEX_LENGTH ? (byte)0 : (byte)1;
        }
    }
}
