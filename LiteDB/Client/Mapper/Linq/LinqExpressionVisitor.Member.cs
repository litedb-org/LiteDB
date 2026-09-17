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
            // Generated mode never discovers members at runtime: a type without a registered generated
            // map is rejected by GetGeneratedEntityMapper instead of falling back to reflection.
            var entity = _useGeneratedMappers
                ? _mapper.GetGeneratedEntityMapper(entityType)
                : this.GetRuntimeEntityMapper(entityType);
            entity.WaitForInitialization();

            var field = entity.Members.FirstOrDefault(x => x.MemberName == name);
            memberMapper = field ?? throw new NotSupportedException($"Member {name} not found on BsonMapper for type {entityType}.");

            _dbRefType = field.IsDbRef ? field.UnderlyingType : null;

            return "." + (isParentDbRef && field.FieldName == "_id" ? "$id" : field.FieldName);
        }

        private const string RuntimeModeJustification =
            "Reached only when _useGeneratedMappers is false. The private constructor makes ForRuntimeMapping the only way to create such a visitor, and that factory carries RequiresUnreferencedCode.";

        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = RuntimeModeJustification)]
        private EntityMapper GetRuntimeEntityMapper(Type entityType)
        {
            if (_useGeneratedMappers) throw new InvalidOperationException("Runtime entity mapping must not be reached by a generated-mapping visitor.");

            return _mapper.GetEntityMapper(entityType);
        }

        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = RuntimeModeJustification)]
        private BsonValue SerializeRuntimeConstant(object value)
        {
            if (_useGeneratedMappers) throw new InvalidOperationException("Runtime constant serialization must not be reached by a generated-mapping visitor.");

            return _mapper.Serialize(value.GetType(), value);
        }
    }
}
