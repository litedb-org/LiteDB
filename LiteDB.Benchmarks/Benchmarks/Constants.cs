namespace LiteDB.Benchmarks.Benchmarks
{
    internal static class Constants
    {
        internal const string DATABASE_NAME = "Lite.db";

        internal static class Categories
        {
            internal const string DATA_GEN = nameof(DATA_GEN);
            internal const string QUERIES = nameof(QUERIES);
            internal const string INSERTION = nameof(INSERTION);
            internal const string DELETION = nameof(DELETION);
        }
    }
}