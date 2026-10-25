using System;
using LiteDB.Engine;

namespace LiteDB
{
    /// <summary>
    /// Immutable services owned by a database and shared by its client objects.
    /// </summary>
    internal sealed class LiteDatabaseContext
    {
        public ILiteEngine Engine { get; }

        public BsonMapper Mapper { get; }

        public LiteDatabaseContext(ILiteEngine engine, BsonMapper mapper)
        {
            this.Engine = engine ?? throw new ArgumentNullException(nameof(engine));
            this.Mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        }
    }
}
