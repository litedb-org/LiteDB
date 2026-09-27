namespace LiteDB.Fuzz.Targets;

internal sealed record PowerLossOperation(int Kind, int Id, int Value, int PayloadLength,
    int FileLength, int Pattern);

internal sealed record PowerLossTransaction(PowerLossOperation[] Operations);

internal sealed record PowerLossScenario(string Password, byte[] InitialFile,
    PowerLossTransaction[] Transactions)
{
    internal static PowerLossScenario Generate(Random random, int transactionCount)
    {
        var password = random.Next(2) == 0 ? "power-password" : null;
        var initialLength = random.Next(4096, 14001);
        var initialFile = Bytes(initialLength, random.Next());
        var transactions = new PowerLossTransaction[transactionCount];
        for (var transaction = 0; transaction < transactions.Length; transaction++)
        {
            var operationCount = random.Next(2, 6);
            var operations = new PowerLossOperation[operationCount];
            for (var index = 0; index < operations.Length; index++)
            {
                var kind = random.Next(5);
                var id = random.Next(1, 13);
                var value = random.Next(-50_000, 50_001);
                var payload = new[] { 0, 255, 4095, 4096, 8191, 8192, 16000 }[random.Next(7)];
                var fileLength = random.Next(2048, 22001);
                operations[index] = new PowerLossOperation(kind, id, value, payload,
                    fileLength, random.Next());
            }
            transactions[transaction] = new PowerLossTransaction(operations);
        }
        return new PowerLossScenario(password, initialFile, transactions);
    }

    internal static byte[] Bytes(int length, int pattern) => Enumerable.Range(0, length)
        .Select(index => (byte)(pattern + index * 31 + (index >> 3))).ToArray();
}
