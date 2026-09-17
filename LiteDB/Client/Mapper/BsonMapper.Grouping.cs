using System.Collections.Generic;
using System.Linq;

namespace LiteDB
{
    public partial class BsonMapper
    {
        [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode(AotCompatibility.RuntimeModelMapping)]
        [System.Diagnostics.CodeAnalysis.RequiresDynamicCode(AotCompatibility.RuntimeTypeConstruction)]
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

            // Written directly rather than through RegisterType: this is LiteDB's own plumbing for an
            // ordinary GroupBy, and counting it as a user type registration would make every generated
            // collection on this mapper reject the mapper configuration from then on.
            _customSerializer[interfaceType] = SerializeGrouping;
            _customDeserializer[interfaceType] = DeserializeGrouping;

            if (!_customDeserializer.ContainsKey(concreteType))
            {
                _customSerializer[concreteType] = SerializeGrouping;
                _customDeserializer[concreteType] = DeserializeGrouping;
            }
        }
    }
}
