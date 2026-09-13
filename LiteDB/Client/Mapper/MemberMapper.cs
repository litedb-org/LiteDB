using System;
using System.Reflection;
using static LiteDB.Constants;

namespace LiteDB
{
    /// <summary>
    /// Internal representation for a .NET member mapped to BsonDocument
    /// </summary>
    public class MemberMapper
    {
        /// <summary>
        /// If member is Id, indicate that are AutoId
        /// </summary>
        public bool AutoId { get; set; }

        /// <summary>
        /// Member name
        /// </summary>
        public string MemberName { get; set; }

        /// <summary>
        /// Member returns data type
        /// </summary>
        public Type DataType { get; set; }

        /// <summary>
        /// Converted document field name
        /// </summary>
        public string FieldName { get; set; }

        /// <summary>
        /// Delegate method to get value from entity instance
        /// </summary>
        public GenericGetter Getter { get; set; }

        /// <summary>
        /// Delegate method to set value to entity instance
        /// </summary>
        public GenericSetter Setter { get; set; }

        /// <summary>
        /// When used, can be define a serialization function from entity class to bson value
        /// </summary>
        public Func<object, BsonMapper, BsonValue> Serialize { get; set; }

        /// <summary>
        /// When used, can define a deserialization function from bson value
        /// </summary>
        public Func<BsonValue, BsonMapper, object> Deserialize { get; set; }

        /// <summary>
        /// Is this property an DbRef? Must implement Serialize/Deserialize delegates
        /// </summary>
        public bool IsDbRef { get; set; }

        /// <summary>
        /// Indicate that this property contains an list of elements (IEnumerable)
        /// </summary>
        public bool IsEnumerable { get; set; }

        /// <summary>
        /// When property is an array of items, gets underlying type (otherwise is same type of PropertyType)
        /// </summary>
        public Type UnderlyingType { get; set; }

        /// <summary>
        /// Is this property ignore
        /// </summary>
        public bool IsIgnore { get; set; }
    }
}