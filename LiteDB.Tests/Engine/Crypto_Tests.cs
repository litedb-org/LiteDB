using System.IO;
using System.Linq;
using System.Text;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class Crypto_Tests
    {
        [Fact]
        public void Plain_Datafile()
        {
            var data = new MemoryStream();
            var log = new MemoryStream();

            var settings = new EngineSettings {DataStream = data, LogStream = log};

            using (var e = new LiteEngine(settings))
            {
                this.CreateDatabase(e);

                // find string inside stream
                var dataStr = Encoding.UTF8.GetString(data.ToArray());

                // plain datafile will found strings 
                dataStr.Should().Contain("mycol");
                dataStr.Should().Contain("Mauricio");

                // plain datafile will consume only 4 pages: 1 header, 1 collection, 1 data e 1 index
                (data.Length / 8192).Should().Be(4);
            }
        }

        [Fact]
        public void Crypto_Datafile()
        {
            var data = new MemoryStream();
            var log = new MemoryStream();

            var settings = new EngineSettings {DataStream = data, LogStream = log, Password = "abc"};

            using (var e = new LiteEngine(settings))
            {
                this.CreateDatabase(e);

                // find string inside stream
                var dataStr = Encoding.UTF8.GetString(data.ToArray());

                // encrypted datafile will not found plain strings
                dataStr.Should().NotContain("mycol");
                dataStr.Should().NotContain("Mauricio");

                // but document exists!
                var doc = e.Find("mycol", "_id=1").First();

                doc["name"].AsString.Should().Be("Mauricio");

                // encrypted datafile will consume 5 pages: 1 salt page, 1 header, 1 collection, 1 data e 1 index
                (data.Length / 8192).Should().Be(5);
            }
        }

        private void CreateDatabase(LiteEngine engine)
        {
            engine.Insert("mycol", new[]
            {
                new BsonDocument
                {
                    ["_id"] = 1,
                    ["name"] = "Mauricio"
                }
            }, BsonAutoId.Int32);

            // do checkpoint to use only data file
            engine.Checkpoint();
        }
    }
}