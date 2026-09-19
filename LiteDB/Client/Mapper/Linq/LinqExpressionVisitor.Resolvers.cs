using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace LiteDB
{
    internal partial class LinqExpressionVisitor
    {
        private static readonly Dictionary<Type, ITypeResolver> _resolver = new Dictionary<Type, ITypeResolver>
        {
            [typeof(BsonValue)] = new BsonValueResolver(),
            [typeof(BsonArray)] = new BsonValueResolver(),
            [typeof(BsonDocument)] = new BsonValueResolver(),
            [typeof(Convert)] = new ConvertResolver(),
            [typeof(DateTime)] = new DateTimeResolver(),
            [typeof(Int32)] = new NumberResolver("INT32"),
            [typeof(Int64)] = new NumberResolver("INT64"),
            [typeof(Decimal)] = new NumberResolver("DECIMAL"),
            [typeof(Double)] = new NumberResolver("DOUBLE"),
            [typeof(ICollection)] = new ICollectionResolver(),
            [typeof(IGrouping<,>)] = new GroupingResolver(),
            [typeof(Enumerable)] = new EnumerableResolver(),
            [typeof(MemoryExtensions)] = new MemoryExtensionsResolver(),
            [typeof(Guid)] = new GuidResolver(),
            [typeof(Math)] = new MathResolver(),
            [typeof(Regex)] = new RegexResolver(),
            [typeof(ObjectId)] = new ObjectIdResolver(),
            [typeof(String)] = new StringResolver(),
            [typeof(Nullable)] = new NullableResolver(),
            [typeof(LiteDB.Spatial.Spatial)] = new SpatialResolver(),
            [typeof(LiteDB.SpatialExpressions)] = new SpatialResolver()
        };
    }
}
