using Xunit;

namespace LiteDB.Tests.Issues;

[Collection("SharedDemoDatabase")]
public class Issue2534_Tests
{
    [Fact]
    public void Test() 
    {
        using LiteDatabase database = new(new ConnectionString()
        {
            Filename = "Demo.db",
            Connection = ConnectionType.Shared,
        });

        ILiteCollection<BsonDocument> accounts = database.GetCollection("Issue2534");

        if (accounts.Count() < 3)
        {
            accounts.Insert(new BsonDocument());
            accounts.Insert(new BsonDocument());
            accounts.Insert(new BsonDocument());
        }

        foreach (BsonDocument document in accounts.FindAll())
        {
            accounts.Update(document);
        }
    }
}