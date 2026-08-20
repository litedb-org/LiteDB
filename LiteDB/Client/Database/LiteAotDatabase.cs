using System;
using System.IO;

namespace LiteDB
{
    /// <summary>
    /// Provides typed collection access for explicitly registered entity maps without runtime member discovery.
    /// </summary>
    public sealed class LiteAotDatabase : IDisposable
    {
        private readonly LiteDatabase _database;
        private readonly BsonMapper _mapper;

        /// <summary>
        /// Starts a file-backed database that uses explicit AOT entity maps.
        /// </summary>
        /// <param name="connectionString">The LiteDB connection string.</param>
        /// <param name="mapper">A mapper with every entity map registered through <see cref="BsonMapper.RegisterAotEntityMapper(EntityMapper)"/>.</param>
        public LiteAotDatabase(string connectionString, BsonMapper mapper)
            : this(new LiteDatabase(connectionString, mapper ?? throw new ArgumentNullException(nameof(mapper))), mapper)
        {
        }

        /// <summary>
        /// Starts a stream-backed database that uses explicit AOT entity maps.
        /// </summary>
        /// <param name="stream">The data stream.</param>
        /// <param name="mapper">A mapper with every entity map registered through <see cref="BsonMapper.RegisterAotEntityMapper(EntityMapper)"/>.</param>
        /// <param name="logStream">An optional log stream.</param>
        public LiteAotDatabase(Stream stream, BsonMapper mapper, Stream logStream = null)
            : this(new LiteDatabase(stream, mapper ?? throw new ArgumentNullException(nameof(mapper)), logStream), mapper)
        {
        }

        private LiteAotDatabase(LiteDatabase database, BsonMapper mapper)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        }

        /// <summary>
        /// Gets a typed collection using an explicitly registered AOT entity map.
        /// </summary>
        /// <typeparam name="T">The explicitly mapped entity type.</typeparam>
        /// <param name="name">The collection name.</param>
        /// <param name="autoId">The auto-id type when the entity map has no auto-id member.</param>
        /// <returns>The typed collection.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="name"/> is missing.</exception>
        /// <exception cref="InvalidOperationException">Thrown when no explicit AOT map is registered for <typeparamref name="T"/>.</exception>
        public ILiteCollection<T> GetCollection<T>(string name, BsonAutoId autoId = BsonAutoId.ObjectId)
        {
            if (name.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(name));

            if (_mapper.HasAotEntityMapper(typeof(T)) == false)
            {
                throw new InvalidOperationException($"No explicit AOT entity mapper is registered for '{typeof(T).FullName}'.");
            }

            return _database.GetCollectionCore<T>(name, autoId);
        }

        /// <summary>
        /// Disposes the underlying database.
        /// </summary>
        public void Dispose()
        {
            _database.Dispose();
        }
    }
}
