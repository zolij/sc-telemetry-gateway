using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ScTelemetryGateway.Core;

public sealed record SnapshotValue(JsonNode? Value, DateTimeOffset UpdatedAt, bool LastKnown);
public sealed record NumericStatistics(long Count, double? Min, double? Max, double? Mean,
    double? Last, long InvalidCount);
public sealed record TelemetryEvent(long Sequence, DateTimeOffset ReceivedAt, string Type);
public sealed record LossCounters(long BatchesDropped, long EventsDropped);

public sealed record TelemetryBatch(
    Guid SessionId, Guid StreamId, Guid BatchId, long Sequence,
    DateTimeOffset WindowStartedAt, DateTimeOffset WindowEndedAt, long FramesReceived,
    Dictionary<string, SnapshotValue> Snapshot,
    Dictionary<string, NumericStatistics> Statistics,
    List<TelemetryEvent> Events, LossCounters Loss)
{
    public string Type => "telemetry.batch";
    public int SchemaVersion => 1;
    public string AppVersion => "0.1.0";
}

public static class WireJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32
    };

    public static byte[] Serialize(TelemetryBatch batch) =>
        JsonSerializer.SerializeToUtf8Bytes(batch, Options);
}
