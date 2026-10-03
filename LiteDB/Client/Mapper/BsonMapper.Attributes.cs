using System;
using System.Linq;
using System.Reflection;

namespace LiteDB;

public partial class BsonMapper
{
    private static readonly Type _bsonIdAttribute = typeof(BsonIdAttribute);
    private static readonly Type _bsonIgnoreAttribute = typeof(BsonIgnoreAttribute);
    private static readonly Type _bsonFieldAttribute = typeof(BsonFieldAttribute);
    private static readonly Type _bsonRefAttribute = typeof(BsonRefAttribute);

    private const string KeyAttributeName = "System.ComponentModel.DataAnnotations.KeyAttribute";
    private const string NotMappedAttributeName = "System.ComponentModel.DataAnnotations.Schema.NotMappedAttribute";

    private readonly object _attributeMappingsLock = new();
    private Type[] _idAttributeTypes = Array.Empty<Type>();
    private Type[] _ignoreAttributeTypes = Array.Empty<Type>();
    private string[] _idAttributeNames = Array.Empty<string>();
    private string[] _ignoreAttributeNames = Array.Empty<string>();

    /// <summary>
    /// Enables support for DataAnnotations <c>KeyAttribute</c> and
    /// <c>NotMappedAttribute</c> without requiring a DataAnnotations dependency.
    /// Call this method before the affected entity types are first mapped.
    /// </summary>
    public BsonMapper UseDataAnnotations()
    {
        lock (_attributeMappingsLock)
        {
            AddAttribute(ref _idAttributeNames, KeyAttributeName);
            AddAttribute(ref _ignoreAttributeNames, NotMappedAttributeName);
        }

        return this;
    }

    /// <summary>
    /// Registers an attribute type that identifies an entity ID.
    /// Register attributes before the affected entity types are first mapped.
    /// </summary>
    /// <param name="attributeType">The attribute type to register.</param>
    public void RegisterIdAttribute(Type attributeType)
    {
        ValidateAttributeType(attributeType);
        this.RegisterAttribute(ref _idAttributeTypes, attributeType);
    }

    /// <summary>
    /// Registers an attribute by full name that identifies an entity ID.
    /// Register attributes before the affected entity types are first mapped.
    /// </summary>
    /// <param name="attributeFullName">The full name of the attribute to register.</param>
    public void RegisterIdAttributeByName(string attributeFullName)
    {
        ValidateAttributeName(attributeFullName);
        this.RegisterAttribute(ref _idAttributeNames, attributeFullName);
    }

    /// <summary>
    /// Registers an attribute type that excludes a member from mapping.
    /// Register attributes before the affected entity types are first mapped.
    /// </summary>
    /// <param name="attributeType">The attribute type to register.</param>
    public void RegisterIgnoreAttribute(Type attributeType)
    {
        ValidateAttributeType(attributeType);
        this.RegisterAttribute(ref _ignoreAttributeTypes, attributeType);
    }

    /// <summary>
    /// Registers an attribute by full name that excludes a member from mapping.
    /// Register attributes before the affected entity types are first mapped.
    /// </summary>
    /// <param name="attributeFullName">The full name of the attribute to register.</param>
    public void RegisterIgnoreAttributeByName(string attributeFullName)
    {
        ValidateAttributeName(attributeFullName);
        this.RegisterAttribute(ref _ignoreAttributeNames, attributeFullName);
    }

    private AttributeMappings GetAttributeMappings()
    {
        lock (_attributeMappingsLock)
        {
            return new AttributeMappings(
                _idAttributeTypes,
                _ignoreAttributeTypes,
                _idAttributeNames,
                _ignoreAttributeNames);
        }
    }

    private void RegisterAttribute<T>(ref T[] attributes, T attribute)
    {
        lock (_attributeMappingsLock)
        {
            AddAttribute(ref attributes, attribute);
        }
    }

    private static void AddAttribute<T>(ref T[] attributes, T attribute)
    {
        if (attributes.Contains(attribute))
        {
            return;
        }

        var updated = new T[attributes.Length + 1];
        Array.Copy(attributes, updated, attributes.Length);
        updated[attributes.Length] = attribute;
        attributes = updated;
    }

    private static bool HasRegisteredAttribute(MemberInfo member, Type[] types, string[] names)
    {
        if (types.Any(type => Attribute.IsDefined(member, type, true)))
        {
            return true;
        }

        if (names.Length == 0)
        {
            return false;
        }

        return Attribute.GetCustomAttributes(member, true)
            .Any(attribute => names.Contains(attribute.GetType().FullName));
    }

    private static void ValidateAttributeType(Type attributeType)
    {
        if (attributeType == null)
        {
            throw new ArgumentNullException(nameof(attributeType));
        }

        if (!typeof(Attribute).IsAssignableFrom(attributeType))
        {
            throw new ArgumentException("Type must be an Attribute type", nameof(attributeType));
        }
    }

    private static void ValidateAttributeName(string attributeFullName)
    {
        if (string.IsNullOrWhiteSpace(attributeFullName))
        {
            throw new ArgumentException("Attribute name cannot be null or empty", nameof(attributeFullName));
        }
    }

    private readonly struct AttributeMappings
    {
        public Type[] IdTypes { get; }
        public Type[] IgnoreTypes { get; }
        public string[] IdNames { get; }
        public string[] IgnoreNames { get; }

        public AttributeMappings(Type[] idTypes, Type[] ignoreTypes, string[] idNames, string[] ignoreNames)
        {
            this.IdTypes = idTypes;
            this.IgnoreTypes = ignoreTypes;
            this.IdNames = idNames;
            this.IgnoreNames = ignoreNames;
        }
    }
}
