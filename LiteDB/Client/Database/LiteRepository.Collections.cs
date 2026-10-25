using System.Collections.Generic;

namespace LiteDB
{
    public partial class LiteRepository
    {
        /// <summary>Insert the documents in an array and return the inserted count.</summary>
        public int Insert<T>(T[] entities, string collectionName = null) =>
            this.Insert<T>((IEnumerable<T>)entities, collectionName);

        /// <summary>Insert the documents in a list and return the inserted count.</summary>
        public int Insert<T>(List<T> entities, string collectionName = null) =>
            this.Insert<T>((IEnumerable<T>)entities, collectionName);

        /// <summary>Update the documents in an array and return the updated count.</summary>
        public int Update<T>(T[] entities, string collectionName = null) =>
            this.Update<T>((IEnumerable<T>)entities, collectionName);

        /// <summary>Update the documents in a list and return the updated count.</summary>
        public int Update<T>(List<T> entities, string collectionName = null) =>
            this.Update<T>((IEnumerable<T>)entities, collectionName);

        /// <summary>Upsert the documents in an array and return the inserted count.</summary>
        public int Upsert<T>(T[] entities, string collectionName = null) =>
            this.Upsert<T>((IEnumerable<T>)entities, collectionName);

        /// <summary>Upsert the documents in a list and return the inserted count.</summary>
        public int Upsert<T>(List<T> entities, string collectionName = null) =>
            this.Upsert<T>((IEnumerable<T>)entities, collectionName);
    }
}
