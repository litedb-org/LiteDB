using System;

namespace LiteDB
{
    public interface ITypeNameBinder
    {
        string GetName(Type type);
        Type GetType(string name);
    }
}