using System;
using System.Collections.Generic;
using Xunit;
using System.Linq;

namespace LiteDB.Tests.Issues
{
    public class Issue2112_Tests
    {
        private readonly BsonMapper _mapper = new BsonMapper();

        [Fact]
        public void Serialize_covariant_collection_has_type()
        {
            IA a = new A { Bs = new List<B> { new B() } };

            var docA = _mapper.Serialize<IA>(a).AsDocument;
            var docB = docA["Bs"].AsArray[0].AsDocument;

            Assert.True(docA.ContainsKey("_type"));
            Assert.True(docB.ContainsKey("_type"));
        }

        [Fact]
        public void Deserialize_covariant_collection_succeed()
        {
            IA a = new A { Bs = new List<B> { new B() } };
            var serialized = _mapper.Serialize<IA>(a);

            var deserialized = _mapper.Deserialize<IA>(serialized);

            Assert.Equal(1, deserialized.Bs.Count);
        }

        interface IA
        {
            // at runtime this will be a List<B>
            IReadOnlyCollection<IB> Bs { get; set; }
        }

        class A : IA
        {
            public IReadOnlyCollection<IB> Bs { get; set; }
        }

        interface IB 
        {

        }

        class B : IB 
        {

        }
    }
}
