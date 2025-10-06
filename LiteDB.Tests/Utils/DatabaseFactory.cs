using System;
using LiteDB;

namespace LiteDB.Tests.Utils
{
    public enum TestDatabaseType
    {
        Default,
        InMemory,
        Disk
    }

    public static class DatabaseFactory
    {
        public static LiteDatabase Create(TestDatabaseType type = TestDatabaseType.Default, string connectionString = null, BsonMapper mapper = null)
        {
            switch (type)
            {
                case TestDatabaseType.Default:
                case TestDatabaseType.InMemory:
                    return mapper is null
                        ? new LiteDatabase(connectionString ?? ":memory:")
                        : new LiteDatabase(connectionString ?? ":memory:", mapper);

                case TestDatabaseType.Disk:
                    if (string.IsNullOrWhiteSpace(connectionString))
                    {
                        throw new ArgumentException("Disk databases require a connection string.", nameof(connectionString));
                    }

                    return mapper is null
                        ? new LiteDatabase(connectionString)
                        : new LiteDatabase(connectionString, mapper);

                default:
                    throw new ArgumentOutOfRangeException(nameof(type), type, null);
            }
        }
    }
}
