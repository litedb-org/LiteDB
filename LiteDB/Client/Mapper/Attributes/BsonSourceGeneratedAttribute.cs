using System;

namespace LiteDB
{
    /// <summary>
    /// Marks an entity class for LiteDB source-generated mapping.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class BsonSourceGeneratedAttribute : Attribute
    {
    }
}
