using System;

namespace LiteDB
{
    public interface ITypeNameBinder
    {
        string GetName(Type type);
        [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode(AotCompatibility.PersistedTypeResolution)]
        Type GetType(string name);
    }
}
