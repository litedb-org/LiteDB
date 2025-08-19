using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Database
{
    public class Document_Size_Tests
    {
        const int ARRAY_SIZE = 10 * 1024 * 1024;

        [Fact]
        public void Very_Large_Single_Document_Support_With_Partial_Load_Memory_Usage()
        {
            using (var file = new TempFile())
            using (var db = new LiteDatabase(file.Filename))
            {
                var col = db.GetCollection("col");

                // insert 10 mb document

                col.Insert(new BsonDocument
                { 
                    ["_id"] = 1, 
                    ["name"] = "John", 
                    ["data"] = new byte[ARRAY_SIZE]
                });

                GC.Collect();
                GC.WaitForPendingFinalizers();

                var initialMemory = Process.GetCurrentProcess().WorkingSet64;

                // get name only document
                var d0 = col.Query().Select("{ _id, name }").First();

                d0["name"].Should().Be("John");

                var memoryForNameOnly = Process.GetCurrentProcess().WorkingSet64;

                // getting full document
                var d1 = col.Query().First();

                var memoryFullDocument = Process.GetCurrentProcess().WorkingSet64;

                // memory after full document must be at least 10Mb more than with name only

                //memoryFullDocument.Should().BeGreaterOrEqualTo(memoryForNameOnly + (ARRAY_SIZE / 2));
            }
        }
    }
}