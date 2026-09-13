using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Implement a simple system collection with input data only (to use Output must inherit this class)
    /// </summary>
    internal class SystemCollection
    {
        private readonly string _name;
        private readonly Func<IEnumerable<BsonDocument>> _input = null;

        public SystemCollection(string name)
        {
            if (!name.StartsWith("$")) throw new ArgumentException("System collection name must starts with $");

            _name = name;
        }

        public SystemCollection(string name, Func<IEnumerable<BsonDocument>> input)
            : this(name)
        {
            _input = input;
        }

        /// <summary>
        /// Get system collection name (must starts with $)
        /// </summary>
        public string Name => _name;

        /// <summary>
        /// Get input data source factory
        /// </summary>
        public virtual IEnumerable<BsonDocument> Input(BsonValue options) => _input();

        /// <summary>
        /// Get output data source factory (must implement in inherit class)
        /// </summary>
        public virtual int Output(IEnumerable<BsonDocument> source, BsonValue options) => throw new LiteException(0, $"{_name} do not support as output collection");

        /// <summary>
        /// Static helper to read options arg as plain value or as document fields
        /// </summary>
        protected static BsonValue GetOption(BsonValue options, string key)
        {
            return GetOption(options, key, null);
        }

        /// <summary>
        /// Static helper to read options arg as plain value or as document fields
        /// </summary>
        protected static BsonValue GetOption(BsonValue options, string key, BsonValue defaultValue)
        {
            if (options != null && options.IsDocument)
            {
                if (options.AsDocument.TryGetValue(key, out var value))
                {
                    if (defaultValue == null || value.Type == defaultValue.Type)
                    {
                        return value;
                    }
                    else
                    {
                        throw new LiteException(0, $"Parameter `{key}` expect {defaultValue.Type} value type");
                    }
                }
                else
                {
                    return defaultValue;
                }
            }
            else
            {
                return defaultValue == null ? options : defaultValue;
            }
        }
    }
}