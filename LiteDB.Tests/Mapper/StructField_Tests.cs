using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Mapper
{
    public class StructFields_Tests
    {
        public struct Point2D
        {
            public int X;
            public int Y;
        }

        [Fact]
        public void Serialize_Struct_Fields()
        {
            var m = new BsonMapper();

            m.IncludeFields = true;

            using (var db = new LiteDatabase(new MemoryStream(), m, new MemoryStream()))
            {
                var col = db.GetCollection<Point2D>("mytable");

                col.Insert(new Point2D { X = 10, Y = 120 });
                col.Insert(new Point2D { X = 15, Y = 130 });
                col.Insert(new Point2D { X = 20, Y = 140 });

                var col2 = db.GetCollection<Point2D>("mytable");

                var allY = col2.Query().Select(p => p.Y).ToArray();
                var allX = col2.Query().Select(p => p.X).ToArray();
                var allStruct = col2.Query().ToArray();
                var allNewStruct = col2.Query().Select(p => new { NewX = p.X, NewY = p.Y }).ToArray();

            }
        }
    }
}