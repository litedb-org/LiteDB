using System;
using System.Collections.Generic;
using Xunit;

namespace LiteDB.Tests.Issues;

public class Issue546_Tests
{
    [Fact]
    public void Test()
    {
        using LiteDatabase dataBase = new("demo.db");
        ILiteCollection<GuidDictContainer> guidDictCollection = dataBase.GetCollection<GuidDictContainer>("Issue546_Guid_Keys");

        guidDictCollection.DeleteAll();
        guidDictCollection.Insert(new GuidDictContainer());

        Assert.Single(guidDictCollection.FindAll());
    }

    private class GuidDictContainer
    {
        public Dictionary<Guid, string> GuidDict { get; set; } = new()
        {
            [Guid.NewGuid()] = "test",
        };
        public Dictionary<TestEnum, string> EnumDict { get; set; } = new()
        {
            [TestEnum.ThingA] = "test",
        };
    }
    private enum TestEnum
    {
        ThingA,
        ThingB,
    }
}