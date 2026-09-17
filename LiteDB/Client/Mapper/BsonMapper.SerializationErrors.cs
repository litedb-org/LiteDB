using System;

namespace LiteDB
{
    public partial class BsonMapper
    {
        private static LiteException MissingConstructorParameterNames(Type type) =>
            new LiteException(LiteException.INVALID_CTOR,
                "Constructor parameter names are missing for '{0}'. Preserve constructor metadata when trimming or register a constructor with Entity<T>().Ctor(...).",
                type.FullName);

        private void SerializeMember(BsonDocument document, MemberMapper member, object instance, int depth)
        {
            try
            {
                var value = member.Getter(instance);
                if (value == null && !this.SerializeNullValues && member.FieldName != "_id") return;
                document[member.FieldName] = member.Serialize != null
                    ? member.Serialize(value, this)
                    : this.Serialize(member.DataType, value, depth);
            }
            catch (Exception error)
            {
                throw new LiteException((error as LiteException)?.ErrorCode ?? LiteException.MAPPING_ERROR,
                    error, "Error serializing '{0}.{1}': {2} Use BsonIgnore or RegisterType for unsupported members.",
                    instance.GetType().FullName, member.MemberName, error.Message);
            }
        }
    }
}
