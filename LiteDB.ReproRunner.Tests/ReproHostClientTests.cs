using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using LiteDB.ReproRunner.Cli.Execution;
using LiteDB.ReproRunner.Shared.Messaging;

namespace LiteDB.ReproRunner.Tests;

public sealed class ReproHostClientTests
{
    [Fact]
    public async Task SendLogAsync_WritesStructuredEnvelope()
    {
        var writer = new StringWriter();
        var client = new ReproHostClient(writer, TextReader.Null, CreateOptions());

        await client.SendLogAsync("hello world", ReproHostLogLevel.Warning);

        var output = writer.ToString().Trim();
        Assert.True(ReproHostMessageEnvelope.TryParse(output, out var envelope, out _));
        Assert.NotNull(envelope);
        Assert.Equal(ReproHostMessageTypes.Log, envelope!.Type);
        Assert.Equal(ReproHostLogLevel.Warning, envelope.Level);
        Assert.Equal("hello world", envelope.Text);
    }

    [Fact]
    public async Task SendConfigurationAsync_WritesConfigurationEnvelope()
    {
        var writer = new StringWriter();
        var client = new ReproHostClient(writer, TextReader.Null, CreateOptions());

        await client.SendConfigurationAsync(true, "5.0.20");

        var output = writer.ToString().Trim();
        Assert.True(ReproHostMessageEnvelope.TryParse(output, out var envelope, out _));
        Assert.NotNull(envelope);
        Assert.Equal(ReproHostMessageTypes.Configuration, envelope!.Type);

        var payload = envelope.DeserializePayload<ReproHostConfigurationPayload>();
        Assert.NotNull(payload);
        Assert.True(payload!.UseProjectReference);
        Assert.Equal("5.0.20", payload.LiteDBPackageVersion);
    }

    [Fact]
    public async Task ReadAsync_ParsesHostReadyHandshake()
    {
        var envelope = ReproInputEnvelope.CreateHostReady("run-123", "/tmp/shared", 1, 2, "Issue_1234");
        var json = JsonSerializer.Serialize(envelope, CreateOptions());
        using var reader = new StringReader(json + Environment.NewLine);
        var client = new ReproHostClient(TextWriter.Null, reader, CreateOptions());

        var read = await client.ReadAsync();

        Assert.NotNull(read);
        Assert.Equal(ReproInputTypes.HostReady, read!.Type);

        var payload = read.DeserializePayload<ReproHostReadyPayload>();
        Assert.NotNull(payload);
        Assert.Equal("run-123", payload!.RunIdentifier);
        Assert.Equal("/tmp/shared", payload.SharedDatabaseRoot);
        Assert.Equal(1, payload.InstanceIndex);
        Assert.Equal(2, payload.TotalInstances);
        Assert.Equal("Issue_1234", payload.ManifestId);
    }

    [Fact]
    public void TryProcessStructuredLine_NotifiesObserver()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var executor = new ReproExecutor(output, error);
        var observed = new List<ReproHostMessageEnvelope>();
        executor.StructuredMessageObserver = (_, envelope) => observed.Add(envelope);

        var message = ReproHostMessageEnvelope.CreateResult(true, "completed");
        var json = JsonSerializer.Serialize(message, CreateOptions());

        var parsed = executor.TryProcessStructuredLine(json, 0);

        Assert.True(parsed);
        Assert.Single(observed);
        Assert.Equal(ReproHostMessageTypes.Result, observed[0].Type);
        Assert.True(observed[0].Success);
        Assert.Equal("completed", observed[0].Text);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
