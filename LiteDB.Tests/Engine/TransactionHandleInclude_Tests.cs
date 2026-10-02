using System;
using System.Linq;
using LiteDB.Vector;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class TransactionHandleInclude_Tests
    {
        public class Child { public int Id { get; set; } public string Name { get; set; } }
        public class Parent { public int Id { get; set; } public Child Child { get; set; } }
        public class Vec { public int Id { get; set; } public float[] Value { get; set; } }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Include_vector_scores_and_Into_keep_exact_handle_binding(bool shared)
        {
            using var file = new TempFile();
            var mapper = new BsonMapper();
            mapper.Entity<Parent>().DbRef(x => x.Child, "children");
            using (var db = new LiteDatabase(new ConnectionString { Filename = file,
                Connection = shared ? ConnectionType.Shared : ConnectionType.Direct }, mapper))
            {
                using var tx = db.BeginTransaction();
                var children = tx.GetCollection<Child>("children");
                var parents = tx.GetCollection<Parent>("parents");
                children.Insert(new Child { Id = 1, Name = "uncommitted" });
                parents.Insert(new Parent { Id = 1, Child = new Child { Id = 1 } });
                Assert.Equal("uncommitted", parents.Include(x => x.Child).FindById(1).Child.Name);
                Assert.Equal("uncommitted", parents.Query().Include(x => x.Child).Single().Child.Name);
                var vectors = tx.GetCollection<Vec>("vectors");
                vectors.Insert(new Vec { Id = 1, Value = new[] { 1f, 0f } });
                Assert.Single(vectors.Query().TopKNear(x => x.Value, new[] { 1f, 0f }, 1).WithScore());
                Assert.Equal(1, vectors.Query().Into("copy"));
                Assert.Equal(1, tx.GetCollection("copy").Count());
                tx.Rollback();
            }
            using var cold = new LiteDatabase(file);
            foreach (var name in new[] { "children", "parents", "vectors", "copy" })
                Assert.Equal(0, cold.GetCollection(name).Count());
        }
    }
}
