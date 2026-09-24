using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using LiteDB.Tests.CustomMapper.Types;

namespace LiteDB.Tests.CustomMapper
{
    public class CollectionMapperClass:BsonMapper
    {
        public override object Deserialize(Type type, BsonValue value)
        {
            if (type.GetInterfaces().Any(s => s == typeof(ICollectionClass)))
                return DeserializeCollectionObject(type, (BsonDocument)value);
            return base.Deserialize(type, value);
        }

        private object DeserializeCollectionObject(Type type, BsonDocument value)
        {
            var array = base.Deserialize(type,(BsonArray) value["_items"]);
            DeserializeObject(type, array, value);
            return array;
        }

        public override BsonValue Serialize(Type type, object obj, int depth)
        {
            if (obj is ICollectionClass)
            {
                return SerializeCollectionClass(type,obj,depth);
            }
            return base.Serialize(type, obj, depth);
        }

        private BsonDocument SerializeCollectionClass(Type type, object obj, int depth)
        {
            var doc = SerializeObject(type, obj, depth);
            doc["_items"] = SerializeArray(GetListItemType(type, obj),(IEnumerable) obj, depth);
            return doc;
        }
    }
}
