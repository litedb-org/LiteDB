using System;

namespace LiteDB
{
    public partial class LiteQueryable<T>
    {
        private Func<BsonValue, K> CreateGeneratedGroupingKeyDeserializer<K>()
        {
            if (_mapper.TryGetGeneratedExecutionMap<K>(out var map))
            {
                var options = _mapper.ValidateGeneratedExecutionConfiguration(map);
                return value => map.Deserialize(value.AsDocument, options);
            }

            if (GeneratedScalarConverter.CanConvert(typeof(K)))
            {
                return GeneratedScalarConverter.Convert<K>;
            }

            throw new NotSupportedException($"Grouping key type '{typeof(K).FullName}' requires a registered generated execution map.");
        }
    }
}
