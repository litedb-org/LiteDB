namespace LiteDB
{
    internal static class AotCompatibility
    {
        public const string RuntimeModelMapping = "Runtime entity mapping discovers constructors and members on application model types, which trimming cannot preserve automatically.";
        public const string RuntimeTypeConstruction = "Runtime entity mapping constructs arrays or closed generic collection types from runtime Type values, whose native code might not be available.";
        public const string RuntimeCollectionNameResolution = "Default collection-name resolution inspects runtime type interfaces and generic arguments, which trimming cannot preserve automatically.";
        public const string PersistedTypeResolution = "Resolving persisted type names requires runtime type lookup and cannot guarantee that the resolved type is preserved.";
    }
}
