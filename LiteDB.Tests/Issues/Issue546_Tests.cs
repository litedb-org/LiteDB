using System;
using System.Collections.Generic;
using System.Xml.Linq;
using Xunit;

namespace LiteDB.Tests.Issues;

public class Issue546_Tests
{
    [Fact]
    public void Test()
    {
        Assert.NotNull(BsonMapper.Global.Deserialize(typeof(TestEnum), "ThingA"));
        Assert.NotNull(BsonMapper.Global.Deserialize(typeof(TestEnum), JsonSerializer.Deserialize("\"ThingA\"")));

        using LiteDatabase dataBase = new("demo.db");
        ILiteCollection<DictContainer> dictCollection = dataBase.GetCollection<DictContainer>("Issue546_Guid_Keys");

        dictCollection.DeleteAll();
        dictCollection.Insert(new DictContainer());

        Assert.Single(dictCollection.FindAll());
    }

    private class DictContainer {
        public Dictionary<Guid, string> GuidDict { get; set; } = new()
        {
            [Guid.NewGuid()] = "test",
        };
        public Dictionary<TestEnum, string> EnumDict { get; set; } = new()
        {
            [TestEnum.ThingA] = "test",
        };
        public Dictionary<(int A, string B), string> TupleDict { get; set; } = new()
        {
            [(2, "xxx")] = "test",
        };
    }
    private enum TestEnum
    {
        ThingA,
        ThingB,
    }
}