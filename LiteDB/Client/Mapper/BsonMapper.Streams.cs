using System;
using System.IO;
using System.Threading;

namespace LiteDB
{
    public partial class BsonMapper
    {
        private readonly AsyncLocal<StreamReferenceMapper> _streamReferenceMapper = new AsyncLocal<StreamReferenceMapper>();

        /// <summary>
        /// Use a database-scoped mapper while serializing or deserializing model streams.
        /// </summary>
        internal IDisposable UseStreamReferences(StreamReferenceMapper mapper)
        {
            var previous = _streamReferenceMapper.Value;
            _streamReferenceMapper.Value = mapper;

            return new StreamReferenceScope(this, previous);
        }

        private bool TrySerializeStream(object value, out BsonValue result)
        {
            if (value is Stream stream && _streamReferenceMapper.Value != null)
            {
                result = _streamReferenceMapper.Value.Serialize(stream);
                return true;
            }

            result = null;
            return false;
        }

        private bool TryDeserializeStream(Type type, BsonValue value, out object result)
        {
            if (typeof(Stream).IsAssignableFrom(type) == false || _streamReferenceMapper.Value == null)
            {
                result = null;
                return false;
            }

            var stream = _streamReferenceMapper.Value.Deserialize(value);

            if (type.IsAssignableFrom(stream.GetType()))
            {
                result = stream;
                return true;
            }

            stream.Dispose();

            throw new LiteException(LiteException.MAPPING_ERROR,
                $"FileStorage streams cannot be assigned to '{type.FullName}'. Declare the model property as Stream.");
        }

        private sealed class StreamReferenceScope : IDisposable
        {
            private readonly BsonMapper _mapper;
            private readonly StreamReferenceMapper _previous;

            public StreamReferenceScope(BsonMapper mapper, StreamReferenceMapper previous)
            {
                _mapper = mapper;
                _previous = previous;
            }

            public void Dispose()
            {
                _mapper._streamReferenceMapper.Value = _previous;
            }
        }
    }
}
