namespace LiteDB
{
    /// <summary>
    /// All supported BsonTypes supported in AutoId insert operation
    /// </summary>
    public enum BsonAutoId
    {
        Int32 = 2,
        Int64 = 3,
        ObjectId = 10,
        Guid = 11
    }
}