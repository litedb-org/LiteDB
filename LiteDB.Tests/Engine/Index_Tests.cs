using System;
using System.Linq;
using AwesomeAssertions;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class Index_Tests
    {
        [Fact]
        public void Index_With_No_Name()
        {
            using (var db = new LiteDatabase("filename=:memory:"))
            {
                var users = db.GetCollection("users");
                var indexes = db.GetCollection("$indexes");

                users.Insert(new BsonDocument { ["name"] = new BsonDocument { ["first"] = "John", ["last"] = "Doe" } });
                users.Insert(new BsonDocument { ["name"] = new BsonDocument { ["first"] = "Marco", ["last"] = "Pollo" } });

                // no index name defined
                users.EnsureIndex("name.last");
                users.EnsureIndex("$.name.first", true);

                // default name: remove all non-[a-z] chars
                indexes.Invoking(indexCol => indexCol.FindOne("collection = 'users' AND name = 'namelast'")).Should().NotBeNull();
                indexes.Invoking(indexCol => indexCol.FindOne("collection = 'users' AND name = 'namefirst'")).Should().NotBeNull();
            }
        }

        [Fact]
        public void Index_Order()
        {
            using (var db = new LiteDatabase("filename=:memory:"))
            {
                var col = db.GetCollection("col");
                var indexes = db.GetCollection("$indexes");

                col.Insert(new BsonDocument { { "text", "D" } });
                col.Insert(new BsonDocument { { "text", "A" } });
                col.Insert(new BsonDocument { { "text", "E" } });
                col.Insert(new BsonDocument { { "text", "C" } });
                col.Insert(new BsonDocument { { "text", "B" } });

                col.EnsureIndex("text");

                var asc = string.Join("", col.Query()
                    .OrderBy("text")
                    .Select("text")
                    .ToDocuments()
                    .Select(x => x["text"].AsString));

                var desc = string.Join("", col.Query()
                    .OrderByDescending("text")
                    .Select("text")
                    .ToDocuments()
                    .Select(x => x["text"].AsString));

                asc.Should().Be("ABCDE");
                desc.Should().Be("EDCBA");

                var rr = indexes.Query().ToList();

                indexes.Count("name = 'text'").Should().Be(1);
            }
        }

        [Fact]
        public void Index_With_Like()
        {
            using (var db = new LiteDatabase("filename=:memory:"))
            {
                var col = db.GetCollection("names", BsonAutoId.Int32);

                col.Insert(new[]
                {
                    new BsonDocument {["name"] = "marcelo"},
                    new BsonDocument {["name"] = "mauricio"},
                    new BsonDocument {["name"] = "Mauricio"},
                    new BsonDocument {["name"] = "MAUricio"},
                    new BsonDocument {["name"] = "MAURICIO"},
                    new BsonDocument {["name"] = "mauRO"},
                    new BsonDocument {["name"] = "ANA"}
                });

                col.EnsureIndex("idx_name", "name", false);

                var all = db.Execute("SELECT name FROM names").ToArray();

                // LIKE are case insensitive

                var r0 = db.Execute("SELECT name FROM names WHERE name LIKE 'Mau%'").ToArray();
                var r1 = db.Execute("SELECT name FROM names WHERE name LIKE 'MAU%'").ToArray();
                var r2 = db.Execute("SELECT name FROM names WHERE name LIKE 'mau%'").ToArray();

                r0.Length.Should().Be(5);
                r1.Length.Should().Be(5);
                r2.Length.Should().Be(5);

                // only `mauricio´
                var r3 = db.Execute("SELECT name FROM names WHERE name LIKE 'ma%ci%'").ToArray();
                var r4 = db.Execute("SELECT name FROM names WHERE name LIKE 'maUriCIO").ToArray();

                r3.Length.Should().Be(4);
                r4.Length.Should().Be(4);

                var r5 = db.Execute("SELECT name FROM names WHERE name LIKE 'marc_o").ToArray();

                r5.Length.Should().Be(0);

                // `marcelo`
                var r6 = db.Execute("SELECT name FROM names WHERE name LIKE 'marc__o").ToArray();

                r6.Length.Should().Be(1);
            }
        }

        [Fact]
        public void EnsureIndex_Invalid_Arguments()
        {
            using var db = new LiteDatabase("filename=:memory:");
            var test = db.GetCollection("test");

            // null name
            {
                var exn = Assert.Throws<ArgumentNullException>(() => test.EnsureIndex(null, "x", false));
                Assert.Equal("name", exn.ParamName);
            }

            // null expression 1
            {
                var exn = Assert.Throws<ArgumentNullException>(() => test.EnsureIndex(null, false));
                Assert.Equal("expression", exn.ParamName);
            }

            // null expression 2
            {
                var exn = Assert.Throws<ArgumentNullException>(() => test.EnsureIndex("x", null, false));
                Assert.Equal("expression", exn.ParamName);
            }
        }

        [Fact]
        public void MultiKey_Index_Test()
        {
            using var db = new LiteDatabase("filename=:memory:");
            var col = db.GetCollection("customers", BsonAutoId.Int32);
            col.EnsureIndex("$.Phones[*].Type");

            var doc1 = new BsonDocument
            {
                ["Name"] = "John Doe",
                ["Phones"] = new BsonArray
                (
                    new BsonDocument 
                    {
                        ["Type"] = "Mobile",
                        ["Number"] = "9876-5432"
                    },
                    new BsonDocument
                    {
                        ["Type"] = "Fixed",
                        ["Number"] = "3333-3333"
                    }
                )
            };

            var doc2 = new BsonDocument
            {
                ["Name"] = "Jane Doe",
                ["Phones"] = new BsonArray
                (
                    new BsonDocument
                    {
                        ["Type"] = "Fixed",
                        ["Number"] = "3000-0000"
                    }
                )
            };

            col.Insert(doc1);
            col.Insert(doc2);

            var query1 = "select $ from customers where $.Phones[*].Type any = 'Mobile'";
            var query2 = "select $ from customers where $.Phones[*].Type any = 'Fixed'";

            var explain1 = db.Execute("explain " + query1).First();
            Assert.True(!explain1["index"]["mode"].AsString.Contains("_id"));

            var explain2 = db.Execute("explain " + query2).First();
            Assert.True(!explain2["index"]["mode"].AsString.Contains("_id"));


            var result1 = db.Execute(query1).ToArray();
            Assert.True(result1.Length == 1);

            var result2 = db.Execute(query2).ToArray();
            Assert.True(result2.Length == 2);
        }
    }
}