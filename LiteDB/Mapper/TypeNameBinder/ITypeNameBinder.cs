using System;

namespace LiteDB
{
    /// <summary>Controls the names and permitted types used by polymorphic mapping.</summary>
    public interface ITypeNameBinder
    {
        /// <summary>Returns the discriminator to store for a concrete type.</summary>
        string GetName(Type type);

        /// <summary>Resolves an allowed discriminator, or returns null when it is unknown.</summary>
        Type GetType(string name);
    }
}
