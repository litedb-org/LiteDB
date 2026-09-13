using System;

namespace LiteDB.Engine
{
    /// <summary>
    /// Interface for abstract document lookup that can be direct from datafile or by virtual collections
    /// </summary>
    internal interface IDocumentLookup
    {
        BsonDocument Load(IndexNode node);
        BsonDocument Load(PageAddress rawId);
    }
}