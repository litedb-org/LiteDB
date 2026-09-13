using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /*public class Create_Database_Tests
    {
        [Fact]
        public void Create_Database_With_Initial_Size()
        {
            var initial = 163840; // initial size: 20 x 8192 = 163.840 bytes
            var minimal = 8192 * 4; // 1 header + 1 collection + 1 data + 1 index = 4 pages minimal

            using (var file = new TempFile())
            using (var db = new LiteEngine(new EngineSettings { Filename = file.Filename, InitialSize = initial }))
            {
                // test if file has 40kb
                Assert.Equal(initial, file.Size);

                // insert minimal data
                db.Insert("col1", new BsonDocument { ["a"] = 1 });

                Assert.Equal(initial, file.Size);

                // ok, now shrink and test if file are minimal size
                db.Shrink();
                
                Assert.Equal(minimal, file.Size);
            }
        }
    }*/
}