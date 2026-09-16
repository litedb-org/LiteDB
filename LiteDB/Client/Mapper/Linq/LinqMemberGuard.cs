using System;

namespace LiteDB
{
    // EntityMapper and MemberMapper are publicly mutable, including their member
    // list. Validate the exact metadata translation consulted before reusing IR.
    internal sealed class LinqMemberGuard
    {
        private readonly EntityMapper _entity;
        private readonly MemberMapper _member;
        private readonly string _name;
        private readonly string _field;
        private readonly bool _dbRef;
        private readonly Type _underlying;
        private readonly Type _dataType;
        private readonly string _collection;

        internal LinqMemberGuard(EntityMapper entity, MemberMapper member)
        {
            _entity = entity;
            _member = member;
            _name = member.MemberName;
            _field = member.FieldName;
            _dbRef = member.IsDbRef;
            _underlying = member.UnderlyingType;
            _dataType = member.DataType;
            _collection = member.DbRefCollectionName;
        }

        internal bool IsCurrent()
        {
            foreach (var member in _entity.Members)
            {
                if (member.MemberName != _name) continue;
                return ReferenceEquals(member, _member) && member.FieldName == _field && member.IsDbRef == _dbRef &&
                    member.UnderlyingType == _underlying && member.DataType == _dataType && member.DbRefCollectionName == _collection;
            }
            return false;
        }
    }
}
