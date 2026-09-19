namespace LiteDB
{
    public partial class LiteQueryable<T>
    {
        /// <summary>
        /// Find an entity by id, or return the default value if it is missing.
        /// </summary>
        public T SingleOrDefaultById(BsonValue id)
        {
            return this.Where("_id = @0", id).SingleOrDefault();
        }
    }
}
