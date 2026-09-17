using System;

namespace LiteDB.AotTests;

[BsonSourceGenerated]
public sealed class ScalarRecord
{
    public int Id { get; set; }
    public bool Enabled { get; set; }
    public int Count { get; set; }
    public long Total { get; set; }
    public double Ratio { get; set; }
    public decimal Amount { get; set; }
    public DateTime Timestamp { get; set; }
    public Guid CorrelationId { get; set; }
    public byte[] Payload { get; set; } = [];
}
