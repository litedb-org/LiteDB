using System.Collections.Generic;
using System.Linq;

namespace LiteDB
{
    public partial class BsonMapper
    {
        internal void RegisterGroupingType<TKey, TElement>()
        {
            var interfaceType = typeof(IGrouping<TKey, TElement>);
            var concreteType = typeof(LiteGrouping<TKey, TElement>);

            if (_customDeserializer.ContainsKey(interfaceType))
            {
                return;
            }

            BsonValue SerializeGrouping(object value)
            {
                var grouping = (IGrouping<TKey, TElement>)value;
                var items = new BsonArray();

                foreach (var item in grouping)
                {
                    items.Add(this.Serialize(typeof(TElement), item));
                }

                return new BsonDocument
                {
                    [LiteGroupingFieldNames.Key] = this.Serialize(typeof(TKey), grouping.Key),
                    [LiteGroupingFieldNames.Items] = items
                };
            }

            object DeserializeGrouping(BsonValue value)
            {
                var document = value.AsDocument;

                var key = (TKey)this.Deserialize(typeof(TKey), document[LiteGroupingFieldNames.Key]);

                var itemsArray = document[LiteGroupingFieldNames.Items].AsArray;
                var items = new List<TElement>(itemsArray.Count);

                foreach (var item in itemsArray)
                {
                    items.Add((TElement)this.Deserialize(typeof(TElement), item));
                }

                return new LiteGrouping<TKey, TElement>(key, items);
            }

            this.RegisterType(interfaceType, SerializeGrouping, DeserializeGrouping);

            if (!_customDeserializer.ContainsKey(concreteType))
            {
                this.RegisterType(concreteType, SerializeGrouping, DeserializeGrouping);
            }
        }
    }
}
