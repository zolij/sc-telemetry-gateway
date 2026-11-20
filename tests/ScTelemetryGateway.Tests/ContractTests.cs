using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using ScTelemetryGateway.Core;
using ScTelemetryGateway.Windows;

namespace ScTelemetryGateway.Tests;

public sealed class ContractTests
{
    [Fact]
    public void SerializedBatchesAndAcksMatchPublishedSchema()
    {
        var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "telemetry.schema.json")));
        var store = new TelemetryAggregator(["actor.state.uec_balance", "actor.vitals.heart_rate"], Guid.NewGuid());
        store.Ingest(new() { Actor = new() { State = new() { UecBalance = long.MaxValue }, Vitals = new() { HeartRate = float.NaN } } });
        var node = JsonNode.Parse(WireJson.Serialize(store.CloseWindow(0)!))!;
        Assert.True(schema.Evaluate(JsonSerializer.SerializeToElement(node)).IsValid);
        using var ack = JsonDocument.Parse($$"""{"type":"telemetry.ack","schema_version":1,"batch_id":"{{Guid.NewGuid()}}"}""");
        Assert.True(schema.Evaluate(ack.RootElement).IsValid);
        node["surprise"] = true;
        Assert.False(schema.Evaluate(JsonSerializer.SerializeToElement(node)).IsValid);
    }

    [Theory]
    [InlineData("ws://receiver.example/path")]
    [InlineData("https://receiver.example/path")]
    [InlineData("wss://user:secret@receiver.example/path")]
    [InlineData("wss://receiver.example/path?key=secret")]
    [InlineData("wss://receiver.example/path#fragment")]
    public void RejectsInsecureOrCredentialBearingEndpoints(string url) =>
        Assert.Throws<ArgumentException>(() => new GatewayOptions { Endpoint = url }.Validate(false));

    [Fact]
    public void RejectsUnknownAndNullConfigurationRatherThanUsingDefaults()
    {
        var options = JsonSerializer.Deserialize<GatewayOptions>("""{"fields":null}""", WireJson.Options)!;
        Assert.Throws<ArgumentException>(() => options.Validate(false));
        Assert.Throws<ArgumentException>(() => new GatewayOptions { Fields = ["actor.future.secret"] }.Validate(false));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<GatewayOptions>("""{"unknown":true}""", WireJson.Options));
    }

    [WindowsFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void SavesProtectedKeyAndSurfacesCorruption()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var path = Path.Combine(Path.GetTempPath(), "sctg-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(path);
            const string key = "synthetic-secret-for-dpapi-test";
            var options = new GatewayOptions { Endpoint = "wss://example.org/custom", Fields = ["actor.state.locomotion"] };
            store.Save(options, key);
            Assert.Equal(key, store.Load().Key);
            Assert.DoesNotContain(key, File.ReadAllText(Path.Combine(path, "settings.json")));
            Assert.DoesNotContain(key, Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(path, "api-key.bin"))));
            File.WriteAllBytes(Path.Combine(path, "api-key.bin"), [1, 2, 3]);
            Assert.Throws<CryptographicException>(() => store.Load());
            File.Delete(Path.Combine(path, "api-key.bin"));
            File.WriteAllText(Path.Combine(path, "settings.json"), "{bad");
            Assert.Throws<JsonException>(() => store.Load());
        }
        finally
        {
            if (Directory.Exists(path))
            {
                foreach (var file in Directory.GetFiles(path)) File.Delete(file);
                Directory.Delete(path);
            }
        }
    }
}

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Requires Windows DPAPI.";
    }
}
