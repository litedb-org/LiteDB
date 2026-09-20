using System;
using System.Reflection;

namespace LiteDB
{
    // EntityMapper and MemberMapper are publicly mutable, including their member
    // list. Validate the exact metadata translation consulted before reusing IR.
    internal sealed class LinqMemberGuard
    {
        private readonly BsonMapper _mapper;
        private readonly EntityMapper _entity;
        private readonly MemberMapper _member;
        private readonly MemberInfo _declaredMember;
        private readonly string _name;
        private readonly string _field;
        private readonly bool _dbRef;
        private readonly Type _underlying;
        private readonly Type _dataType;
        private readonly string _collection;
        private readonly string _resolvedField;

        internal LinqMemberGuard(BsonMapper mapper, EntityMapper entity, MemberMapper member, MemberInfo declaredMember, string resolvedField)
        {
            _mapper = mapper;
            _entity = entity;
            _member = member;
            _declaredMember = declaredMember;
            _name = member.MemberName;
            _field = member.FieldName;
            _dbRef = member.IsDbRef;
            _underlying = member.UnderlyingType;
            _dataType = member.DataType;
            _collection = member.DbRefCollectionName;
            _resolvedField = resolvedField;
        }

        internal bool IsCurrent()
        {
            // Merely retaining the old member is insufficient: an inserted member
            // can take precedence in the publicly mutable mapping list.
            if (!ReferenceEquals(SelectedMember(), _member)) return false;
            return _member.MemberName == _name && _member.FieldName == _field && _member.IsDbRef == _dbRef &&
                _member.UnderlyingType == _underlying && _member.DataType == _dataType &&
                _member.DbRefCollectionName == _collection &&
                _mapper.ResolveAbstractIdField(_entity, _member) == _resolvedField;
        }

        private MemberMapper SelectedMember()
        {
            if (_entity.ForType.IsInterface) return _entity.FindMember(_declaredMember);
            // Match FindMember without allocating a predicate on ordinary hits.
            foreach (var member in _entity.Members)
                if (member.MemberName == _declaredMember.Name) return member;
            return null;
        }
    }
}
