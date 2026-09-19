using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using LiteDB.Tests.CustomMapper.Types;
using Xunit;

namespace LiteDB.Tests.CustomMapper
{
    public class Custom_Mapper_Tests
    {
        private BsonMapper _mapper;

        public Custom_Mapper_Tests()
        {
            _mapper = new CollectionMapperClass();
        }

        private ItemCollection CreateCollection()
        {
            var items = new ItemCollection()
            {
                MyItemCollectionName = "MyCollection"
            };
            items.Add(new Item()
            {
                MyItemName = "MyItem"
            });
            return items;
        }
        [Fact]
        public void ShouldSerializeCollectionClass()
        {
            var items = CreateCollection();

            var document = _mapper.ToDocument(items);
            Assert.Equal("MyCollection", (string)document["MyItemCollectionName"]);

            var array = (BsonArray)document["_items"];
            Assert.NotNull(array);
            var recoveritem = (BsonDocument)array[0];
            Assert.Equal("MyItem", (string)recoveritem["MyItemName"]);
        }

        [Fact]
        public void ShouldDeserializeCollectionClass()
        {

            var items = CreateCollection();
            var document = _mapper.ToDocument(items);


            var lst = (ItemCollection)_mapper.Deserialize(typeof(ItemCollection), document);

            Assert.Equal("MyCollection", lst.MyItemCollectionName);
            Assert.Single(lst);
            Assert.IsType<Item>(lst[0]);
            Assert.Equal("MyItem", lst[0].MyItemName);
        }

        [Fact]
        public void ShouldInsertIntoDatabaseAndRecover()
        {
            var items = CreateCollection();
            using (var repository = new LiteRepository(new MemoryStream(), _mapper))
            {
               var result= repository.Upsert<ItemCollection>(items);
                Assert.True(result);
                Assert.NotEqual(Guid.Empty,items.Id);
                var lst = repository.SingleById<ItemCollection>(items.Id);
                Assert.Equal("MyCollection", lst.MyItemCollectionName);
                Assert.Single(lst);
                Assert.IsType<Item>(lst[0]);
                Assert.Equal("MyItem", lst[0].MyItemName);
            }
        }
    }
}
