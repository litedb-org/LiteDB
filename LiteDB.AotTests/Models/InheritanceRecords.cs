using System.Collections.Generic;

#nullable enable
namespace LiteDB.AotTests;

[BsonSourceGenerated]
public sealed class ComputedRecord
{
    public int Id { get; set; }
    public string NodeType { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public string Fingerprint => string.Join("|", NodeType, ContentHash);
}

public abstract class HierarchyExecutionBase
{
    [BsonId(false)]
    public virtual int EntityKey { get; set; }

    [BsonField("base_name")]
    public virtual string DisplayName { get; set; } = string.Empty;

    [BsonIgnore]
    public virtual string? IgnoredValue { get; set; }
}

public abstract class HierarchyExecutionMiddle : HierarchyExecutionBase
{
    public override int EntityKey { get; set; }

    [BsonField("middle_name")]
    public override string DisplayName { get; set; } = string.Empty;

    public override string? IgnoredValue { get; set; }
}

[BsonSourceGenerated]
public sealed class HierarchyExecutionRecord : HierarchyExecutionMiddle
{
    private int _entityKey;

    public override int EntityKey
    {
        get => _entityKey;
        set
        {
            _entityKey = value;
            IdSetterCalls++;
        }
    }

    public override string DisplayName { get; set; } = string.Empty;
    public override string? IgnoredValue { get; set; }
    public int DerivedScore { get; set; }

    [BsonIgnore]
    public string Projection => $"{DisplayName}:{DerivedScore}";

    [BsonIgnore]
    public int IdSetterCalls { get; private set; }
}

public abstract class InheritedRecordBase
{
    [BsonId(false)]
    public int BaseId { get; set; }

    [BsonField("base_name")]
    public string BaseName { get; set; } = string.Empty;

    public List<string> BaseTags { get; set; } = [];

    [BsonIgnore]
    public string? IgnoredBaseValue { get; set; }
}

[BsonSourceGenerated]
public sealed class InheritedRecord : InheritedRecordBase
{
    public string DerivedName { get; set; } = string.Empty;
}

public abstract class MultiLevelInheritedGrandparent
{
    [BsonId(false)]
    public int RootId { get; set; }

    [BsonField("origin")]
    public string Origin { get; set; } = string.Empty;
}

public abstract class MultiLevelInheritedParent : MultiLevelInheritedGrandparent
{
    public string ParentName { get; set; } = string.Empty;

    [BsonIgnore]
    public string? IgnoredParentValue { get; set; }
}

[BsonSourceGenerated]
public sealed class MultiLevelInheritedRecord : MultiLevelInheritedParent
{
    public string DerivedName { get; set; } = string.Empty;
}

public class OverrideRecordBase
{
    [BsonId(false)]
    public virtual int OverrideId { get; set; }

    [BsonField("stored_name")]
    public virtual string Name { get; set; } = string.Empty;

    [BsonIgnore]
    public virtual string? Ignored { get; set; }
}

public class OverrideRecordMiddle : OverrideRecordBase
{
    public override int OverrideId { get; set; }
    public override string Name { get; set; } = string.Empty;
    public override string? Ignored { get; set; }
}

[BsonSourceGenerated]
public sealed class OverrideRecord : OverrideRecordMiddle
{
    private int _overrideId;

    public override int OverrideId
    {
        get => _overrideId;
        set
        {
            _overrideId = value;
            SetterCalls++;
        }
    }

    public override string Name { get; set; } = string.Empty;
    public override string? Ignored { get; set; }

    [BsonIgnore]
    public int SetterCalls { get; private set; }
}

public class SingleLevelExecutionBase
{
    public int Id { get; set; }
    public int BaseValue { get; set; }
}

[BsonSourceGenerated]
public sealed class SingleLevelExecutionRecord : SingleLevelExecutionBase
{
    public string DerivedValue { get; set; } = string.Empty;
}

public class Person
{
    public int PersonId { get; set; }
}

[BsonSourceGenerated]
public sealed class Employee : Person
{
    public string Name { get; set; } = string.Empty;
}

[BsonSourceGenerated]
public sealed class EmployeeWithId : Person
{
    public int Id { get; set; }
}

[BsonSourceGenerated]
public sealed class EmployeeWithExplicitId : Person
{
    public int Id { get; set; }

    [BsonId(false)]
    public int Key { get; set; }
}
