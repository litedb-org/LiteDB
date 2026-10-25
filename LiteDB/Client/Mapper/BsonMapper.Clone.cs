using System;
using System.Linq;

namespace LiteDB
{
    public partial class BsonMapper
    {
        /// <summary>
        /// Create an independent mapper with the same configuration and entity mappings.
        /// Runtime caches are intentionally not shared.
        /// </summary>
        internal BsonMapper Clone()
        {
            var clone = this.CreateCloneInstance();

            if (clone == null || ReferenceEquals(clone, this))
            {
                throw new InvalidOperationException("CreateCloneInstance must return a new BsonMapper instance.");
            }

            clone.SerializeNullValues = this.SerializeNullValues;
            clone.TrimWhitespace = this.TrimWhitespace;
            clone.EmptyStringToNull = this.EmptyStringToNull;
            clone.EnumAsInteger = this.EnumAsInteger;
            clone.IncludeFields = this.IncludeFields;
            clone.IncludeNonPublic = this.IncludeNonPublic;
            clone.MaxDepth = this.MaxDepth;
            clone.ResolveFieldName = this.ResolveFieldName;
            clone.ResolveMember = this.ResolveMember;
            clone.ResolveCollectionName = this.ResolveCollectionName;
            clone.OnDeserialization = this.OnDeserialization;

            clone._customSerializer.Clear();
            clone._customDeserializer.Clear();
            clone._entities.Clear();

            foreach (var item in _customSerializer)
            {
                if (!IsGroupingType(item.Key)) clone._customSerializer[item.Key] = item.Value;
            }

            foreach (var item in _customDeserializer)
            {
                if (!IsGroupingType(item.Key)) clone._customDeserializer[item.Key] = item.Value;
            }

            var entities = _entities.ToArray();

            foreach (var item in entities)
            {
                item.Value.WaitForInitialization();
                clone._entities[item.Key] = new EntityMapper(item.Key);
            }

            foreach (var item in entities)
            {
                CloneEntityMapper(item.Value, clone._entities[item.Key]);
            }

            foreach (var item in entities)
            {
                var source = item.Value;
                var target = clone._entities[item.Key];

                for (var i = 0; i < source.Members.Count; i++)
                {
                    if (source.Members[i].DbRefCollectionName != null)
                    {
                        RegisterDbRef(clone, target.Members[i], clone._typeNameBinder,
                            source.Members[i].DbRefCollectionName);
                    }
                }
            }

            return clone;
        }

        /// <summary>
        /// Create the mapper instance used when cloning this mapper for a database.
        /// Derived mappers can override this to preserve their runtime type and custom state.
        /// </summary>
        protected virtual BsonMapper CreateCloneInstance()
        {
            return new BsonMapper(_typeInstantiator, _typeNameBinder);
        }

        private static void CloneEntityMapper(EntityMapper source, EntityMapper target)
        {
            target.PopulateMembers = source.PopulateMembers;
            target.UsesCustomIdSelection = source.UsesCustomIdSelection;
            target.CreateInstance = source.CreateInstance?.Target is MappedConstructor
                ? null
                : source.CreateInstance;

            foreach (var ignored in source.IgnoredMembers)
            {
                target.IgnoredMembers.Add(ignored);
            }

            foreach (var member in source.Members)
            {
                var isDbRef = member.DbRefCollectionName != null;
                target.Members.Add(new MemberMapper
                {
                    AutoId = member.AutoId,
                    MemberName = member.MemberName,
                    DataType = member.DataType,
                    FieldName = member.FieldName,
                    HasExplicitFieldName = member.HasExplicitFieldName,
                    ReflectedMember = member.ReflectedMember,
                    Getter = member.Getter,
                    Setter = member.Setter,
                    DefaultSetter = member.DefaultSetter,
                    Serialize = isDbRef ? null : member.Serialize,
                    Deserialize = isDbRef ? null : member.Deserialize,
                    IsDbRef = member.IsDbRef,
                    IsEnumerable = member.IsEnumerable,
                    UnderlyingType = member.UnderlyingType,
                    IsIgnore = member.IsIgnore,
                    DbRefCollectionName = member.DbRefCollectionName
                });
            }
        }

        private static bool IsGroupingType(Type type)
        {
            if (!type.IsGenericType) return false;

            var definition = type.GetGenericTypeDefinition();
            return definition == typeof(IGrouping<,>) || definition == typeof(LiteGrouping<,>);
        }
    }
}
