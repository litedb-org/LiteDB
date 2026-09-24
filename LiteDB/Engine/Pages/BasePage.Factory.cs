using System;

namespace LiteDB.Engine
{
    internal partial class BasePage
    {
        public static T ReadPage<T>(PageBuffer buffer)
            where T : BasePage
        {
            if (typeof(T) == typeof(BasePage)) return (T)(object)new BasePage(buffer);
            if (typeof(T) == typeof(HeaderPage)) return (T)(object)new HeaderPage(buffer);
            if (typeof(T) == typeof(CollectionPage)) return (T)(object)new CollectionPage(buffer);
            if (typeof(T) == typeof(IndexPage)) return (T)(object)new IndexPage(buffer);
            if (typeof(T) == typeof(VectorIndexPage)) return (T)(object)new VectorIndexPage(buffer);
            if (typeof(T) == typeof(SchemaPage)) return (T)(object)new SchemaPage(buffer);
            if (typeof(T) == typeof(DataPage)) return (T)(object)new DataPage(buffer);

            throw new InvalidCastException();
        }

        /// <summary>
        /// Create new page instance with new PageID and passed buffer (NEW)
        /// </summary>
        public static T CreatePage<T>(PageBuffer buffer, uint pageID)
            where T : BasePage
        {
            if (typeof(T) == typeof(CollectionPage)) return (T)(object)new CollectionPage(buffer, pageID);
            if (typeof(T) == typeof(IndexPage)) return (T)(object)new IndexPage(buffer, pageID);
            if (typeof(T) == typeof(VectorIndexPage)) return (T)(object)new VectorIndexPage(buffer, pageID);
            if (typeof(T) == typeof(SchemaPage)) return (T)(object)new SchemaPage(buffer, pageID);
            if (typeof(T) == typeof(DataPage)) return (T)(object)new DataPage(buffer, pageID);

            throw new InvalidCastException();
        }

    }
}
