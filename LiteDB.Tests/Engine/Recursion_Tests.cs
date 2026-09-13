using System;
using Xunit;

namespace LiteDB.Tests.Engine;

public class Recursion_Tests
{
    [Fact]
    public void UpdateInFindAll()
    {
        Test(collection =>
        {
            foreach (BsonDocument document in collection.FindAll())
            {
                collection.Update(document);
            }
        });
    }
    [Fact]
    public void InsertDeleteInFindAll()
    {
        Test(collection =>
        {
            foreach (BsonDocument document in collection.FindAll())
            {
                BsonValue id = collection.Insert(new BsonDocument());
                collection.Delete(id);
            }
        });
    }
    [Fact]
    public void QueryInFindAll()
    {
        Test(collection =>
        {
            foreach (BsonDocument document in collection.FindAll())
            {
                collection.Query().Count();
            }
        });
    }

    private void Test(Action<ILiteCollection<BsonDocument>> action)
    {
        using LiteDatabase database = new(new ConnectionString()
        {
            Filename = "Demo.db",
            Connection = ConnectionType.Shared,
        });

        ILiteCollection<BsonDocument> accounts = database.GetCollection("Recursion");

        if (accounts.Count() < 3)
        {
            accounts.Insert(new BsonDocument());
            accounts.Insert(new BsonDocument());
            accounts.Insert(new BsonDocument());
        }
        action(accounts);
    }
}