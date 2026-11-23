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
    public void MissingPortsUseDefaultsAndSavedPortsRemainUnchanged()
    {
        var defaults = JsonSerializer.Deserialize<GatewayOptions>("""{}""", WireJson.Options)!;
        Assert.Equal(29420, defaults.GamePort);
        Assert.Equal(29421, defaults.MfdPort);

        var saved = JsonSerializer.Deserialize<GatewayOptions>(
            """{"game_port":50051,"mfd_port":50052}""", WireJson.Options)!;
        Assert.Equal(50051, saved.GamePort);
        Assert.Equal(50052, saved.MfdPort);
    }

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
    public void EmptyRemoteSelectionCanBeSavedButCannotBeStarted()
    {
        var options = new GatewayOptions { Endpoint = "wss://example.org/telemetry" };
        options.Validate(requireDestination: false);
        var error = Assert.Throws<ArgumentException>(() => options.Validate());
        Assert.Contains("remote telemetry field", error.Message);
        (options with { Fields = ["actor.state.locomotion"] }).Validate();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ClearingOneDestinationPreservesTheOtherAcrossSettingsRoundTrip(bool clearMfd)
    {
        var options = new GatewayOptions
        {
            Endpoint = "wss://example.org/telemetry",
            Fields = clearMfd ? ["actor.state.locomotion"] : [],
            MfdEnabled = true, MfdAddress = "127.0.0.1",
            MfdFields = clearMfd ? [] : ["actor.vitals.heart_rate"]
        };
        options.Validate(requireDestination: false);
        var loaded = JsonSerializer.Deserialize<GatewayOptions>(
            JsonSerializer.Serialize(options, WireJson.Options), WireJson.Options)!;
        loaded.Validate(requireDestination: false);
        Assert.Equal(options.Fields, loaded.Fields);
        Assert.Equal(options.MfdFields, loaded.MfdFields);
        var error = Assert.Throws<ArgumentException>(() => loaded.Validate());
        Assert.Contains(clearMfd ? "MFD telemetry field" : "remote telemetry field", error.Message);
    }

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
