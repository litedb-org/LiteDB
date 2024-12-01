using System.IO;
using Xunit;

namespace LiteDB.Tests.Database;

public class Writing_While_Reading_Test
{
    [Fact]
    public void Test()
    {
        using var f = new TempFile();
        using (var db = new LiteDatabase(f.Filename))
        {
            var col = db.GetCollection<MyClass>("col");
            col.Insert(new MyClass { Name = "John", Description = "Doe" });
            col.Insert(new MyClass { Name = "Joana", Description = "Doe" });
            col.Insert(new MyClass { Name = "Doe", Description = "Doe" });
        }


        using (var db = new LiteDatabase(f.Filename))
        {
            var col = db.GetCollection<MyClass>("col");
            foreach (var item in col.FindAll())
            {
                item.Description += " Changed";
                col.Update(item);
            }

            db.Commit();
        }


        using (var db = new LiteDatabase(f.Filename))
        {
            var col = db.GetCollection<MyClass>("col");
            foreach (var item in col.FindAll())
            {
                Assert.EndsWith("Changed", item.Description);
            }
        }
    }

    class MyClass
    {
        public int Id { get; set; }

        public string Name { get; set; }

        public string Description { get; set; }
    }
}