using System.Collections.Generic;

namespace LiteDB
{
    public partial interface ILiteRepository
    {
        /// <summary>Insert the documents in an array and return the inserted count.</summary>
        int Insert<T>(T[] entities, string collectionName = null);

        /// <summary>Insert the documents in a list and return the inserted count.</summary>
        int Insert<T>(List<T> entities, string collectionName = null);

        /// <summary>Update the documents in an array and return the updated count.</summary>
        int Update<T>(T[] entities, string collectionName = null);

        /// <summary>Update the documents in a list and return the updated count.</summary>
        int Update<T>(List<T> entities, string collectionName = null);

        /// <summary>Upsert the documents in an array and return the inserted count.</summary>
        int Upsert<T>(T[] entities, string collectionName = null);

        /// <summary>Upsert the documents in a list and return the inserted count.</summary>
        int Upsert<T>(List<T> entities, string collectionName = null);
    }
}
