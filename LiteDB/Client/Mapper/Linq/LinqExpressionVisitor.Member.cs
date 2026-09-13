using System;
using System.Linq;
using System.Reflection;

namespace LiteDB
{
    internal partial class LinqExpressionVisitor
    {
        /// <summary>
        /// Returns document field name for some type member
        /// </summary>
        private string ResolveMember(MemberInfo member, out MemberMapper memberMapper)
        {
            var name = member.Name;
            var declaringType = member.DeclaringType ?? throw new NotSupportedException($"Member {name} has no declaring type.");

            // checks if parent field are not DbRef (checks for same dataType)
            var isParentDbRef = _dbRefType != null && declaringType.IsAssignableFrom(_dbRefType);

            // An inherited root member is declared on its base class, but its generated
            // mapping belongs to the concrete root type and includes the flattened member.
            var entityType = _useGeneratedMappers &&
                declaringType.IsAssignableFrom(_rootParameter.Type) &&
                _mapper.HasGeneratedEntityMapper(_rootParameter.Type)
                    ? _rootParameter.Type
                    : declaringType;
            var entity = _useGeneratedMappers && _mapper.HasGeneratedEntityMapper(entityType)
                ? _mapper.GetGeneratedEntityMapper(entityType)
                : _mapper.GetEntityMapper(entityType);
            entity.WaitForInitialization();

            var field = entity.Members.FirstOrDefault(x => x.MemberName == name);
            memberMapper = field ?? throw new NotSupportedException($"Member {name} not found on BsonMapper for type {entityType}.");

            _dbRefType = field.IsDbRef ? field.UnderlyingType : null;

            return "." + (isParentDbRef && field.FieldName == "_id" ? "$id" : field.FieldName);
        }
    }
}
