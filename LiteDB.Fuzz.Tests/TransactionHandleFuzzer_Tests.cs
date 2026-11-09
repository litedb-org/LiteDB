using LiteDB.Fuzz.Targets;
using Xunit;

namespace LiteDB.Fuzz.Tests;

public sealed class TransactionHandleFuzzer_Tests
{
    [Theory]
    [InlineData(2946)] [InlineData(2947)] [InlineData(2948)]
    [InlineData(2949)] [InlineData(2950)] [InlineData(2951)]
    public async Task Three_terminal_outcomes_and_raw_integrity_use_the_selected_protocol_and_password(int seed)
    {
        var root = Path.Combine(Path.GetTempPath(), "litedb-handle-model-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var context = new FuzzContext("transaction-handle", seed, 3, null, root);
            await new TransactionHandleFuzzer().RunAsync(context);
            Assert.Equal(3, context.Steps);
            Assert.Equal(1, context.Metrics["commits"]);
            Assert.Equal(1, context.Metrics["rollbacks"]);
            Assert.Equal(1, context.Metrics["abandonments"]);
            Assert.True((int)context.Metrics["integrityPages"] > 0);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
