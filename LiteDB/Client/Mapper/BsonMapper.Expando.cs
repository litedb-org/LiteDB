using System.Collections.Generic;
using System.Dynamic;

namespace LiteDB
{
    public partial class BsonMapper
    {
        private BsonDocument SerializeExpando(ExpandoObject expando, int depth)
        {
            var document = new BsonDocument();
            foreach (var element in (IDictionary<string, object>)expando)
            {
                if (document.ContainsKey(element.Key))
                    throw new LiteException(0, $"Dictionary keys serialize to the same BSON field name '{element.Key}'.");
                document[element.Key] = Serialize(typeof(object), element.Value, depth);
            }
            return document;
        }
    }
}
