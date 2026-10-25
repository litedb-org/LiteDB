using System;
using System.Collections.Generic;

namespace LiteDB.Tests.Mapper
{
    public interface IWideFuzzRow
    {
        int Value { get; }
        int? Optional { get; }
        string Name { get; }
        DateTime When { get; }
        int[] Items { get; }
        WideFuzzRow Next { get; }
    }

    public class WideFuzzRow : IWideFuzzRow
    {
        public int Id { get; set; }
        public int Value { get; set; }
        public int? Optional { get; set; }
        public string Name { get; set; }
        public DateTime When { get; set; }
        public int[] Items { get; set; }
        public WideFuzzRow Next { get; set; }
        public Dictionary<string, int> Tags { get; set; }
    }

    public class WideFuzzProjection
    {
        public int Value { get; set; }
        public WideFuzzProjection Next { get; set; }

        [BsonRef("wide-fuzz-rows")]
        public WideFuzzRow Item { get; set; }

        [BsonRef("wide-fuzz-rows")]
        public WideFuzzRow[] Items { get; set; }
    }
}
