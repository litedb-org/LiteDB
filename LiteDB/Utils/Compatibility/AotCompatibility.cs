namespace LiteDB
{
    internal static class AotCompatibility
    {
        public const string RuntimeModelMapping = "Runtime entity mapping discovers constructors and members on application model types, which trimming cannot preserve automatically.";
        public const string RuntimeTypeConstruction = "Runtime entity mapping constructs arrays or closed generic collection types from runtime Type values, whose native code might not be available.";
        public const string RuntimeCollectionNameResolution = "Default collection-name resolution inspects runtime type interfaces and generic arguments, which trimming cannot preserve automatically.";
        public const string RuntimeFileIdMapping = "Custom file IDs can require reflection over nested members that trimming cannot preserve. Use FileStorage for string IDs, or register and validate a converter for the complete custom ID type before suppressing this warning.";
        /// <summary>
        /// What the reflection mapper reads from a type it maps: its properties and fields, and a constructor.
        /// Declared on the file id type parameter so the trimmer keeps exactly these members of whatever type an
        /// application uses as file id.
        /// </summary>
        public const System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes FileIdMembers =
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties |
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.NonPublicProperties |
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicFields |
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.NonPublicFields |
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors;

        public const string PersistedTypeResolution = "Resolving persisted type names requires runtime type lookup and cannot guarantee that the resolved type is preserved.";
    }
}
