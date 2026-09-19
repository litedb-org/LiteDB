using System;

namespace LiteDB
{
    // EntityMapper and MemberMapper are publicly mutable, including their member
    // list. Validate the exact metadata translation consulted before reusing IR.
    internal sealed class LinqMemberGuard
    {
        private readonly BsonMapper _mapper;
        private readonly EntityMapper _entity;
        private readonly MemberMapper _member;
        private readonly string _name;
        private readonly string _field;
        private readonly bool _dbRef;
        private readonly Type _underlying;
        private readonly Type _dataType;
        private readonly string _collection;
        private readonly string _resolvedField;

        internal LinqMemberGuard(BsonMapper mapper, EntityMapper entity, MemberMapper member, string resolvedField)
        {
            _mapper = mapper;
            _entity = entity;
            _member = member;
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
            if (!_entity.Members.Contains(_member)) return false;
            return _member.MemberName == _name && _member.FieldName == _field && _member.IsDbRef == _dbRef &&
                _member.UnderlyingType == _underlying && _member.DataType == _dataType &&
                _member.DbRefCollectionName == _collection &&
                _mapper.ResolveAbstractIdField(_entity, _member) == _resolvedField;
        }
    }
}
