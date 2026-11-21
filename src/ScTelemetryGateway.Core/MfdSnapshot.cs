using System.Text.Json.Nodes;
using Sc.Game.Telemetry.V1;

namespace ScTelemetryGateway.Core;

public sealed record MfdFieldValue(JsonNode? Value, DateTimeOffset UpdatedAt, bool LastKnown);

public sealed record MfdSnapshot(
    Guid SessionId, Guid StreamId, long Sequence, string ConnectionStatus,
    DateTimeOffset PublishedAt, DateTimeOffset? LastFrameAt,
    IReadOnlyDictionary<string, MfdFieldValue> Fields)
{
    public string Type => "mfd.snapshot";
    public int SchemaVersion => 1;
}

public sealed class MfdSnapshotStore
{
    public const int MaxMessageBytes = 1024 * 1024;
    public const int MaxClients = 8;

    private readonly object gate = new();
    private readonly TelemetryField[] fields;
    private readonly TimeProvider clock;
    private readonly Guid sessionId = Guid.NewGuid();
    private Guid streamId = Guid.NewGuid();
    private readonly Dictionary<string, MfdFieldValue> snapshot = [];
    private string connectionStatus = "disconnected";
    private DateTimeOffset? lastFrameAt;
    private long sequence;

    public MfdSnapshotStore(IEnumerable<string> selected, TimeProvider? clock = null)
    {
        fields = selected.Select(path => FieldCatalog.All[path]).ToArray();
        this.clock = clock ?? TimeProvider.System;
    }

    public void StartStream()
    {
        lock (gate)
        {
            streamId = Guid.NewGuid();
            snapshot.Clear();
            lastFrameAt = null;
            connectionStatus = "connected";
            sequence++;
        }
    }

    public void SetPaused()
    {
        lock (gate)
        {
            snapshot.Clear();
            lastFrameAt = null;
            connectionStatus = "paused";
            streamId = Guid.NewGuid();
            sequence++;
        }
    }

    public void Disconnected()
    {
        lock (gate)
        {
            connectionStatus = "disconnected";
            foreach (var path in snapshot.Keys.ToArray())
                snapshot[path] = snapshot[path] with { LastKnown = true };
            sequence++;
        }
    }

    public void Ingest(TelemetryStreamResponse frame)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            lastFrameAt = now;
            connectionStatus = "connected";
            foreach (var section in TelemetryStreamResponse.Descriptor.Fields.InFieldNumberOrder())
            {
                if (!section.Accessor.HasValue(frame) || section.Name == "events")
                    continue;
                var prefix = section.Name + ".";
                foreach (var path in snapshot.Keys.Where(p => p.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
                    snapshot.Remove(path);
                foreach (var field in fields.Where(f => f.Path.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    var (value, invalid) = FieldCatalog.Read(frame, field);
                    if (value is not null || invalid > 0)
                        snapshot[field.Path] = new(value, now, false);
                }
            }
            if (frame.Actor?.State?.Locomotion == ActorLocomotionState.OnFoot ||
                frame.Events?.Events.Any(e => e.Type == GameEvent.Types.GameEventType.ActorLeaveSeat) == true)
                foreach (var path in snapshot.Keys.Where(p => p.StartsWith("vehicle.", StringComparison.Ordinal)).ToArray())
                    snapshot[path] = snapshot[path] with { LastKnown = true };
            sequence++;
        }
    }

    public MfdSnapshot Current()
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            var copy = snapshot.ToDictionary(
                pair => pair.Key,
                pair => new MfdFieldValue(pair.Value.Value?.DeepClone(), pair.Value.UpdatedAt, pair.Value.LastKnown),
                StringComparer.Ordinal);
            return new(sessionId, streamId, sequence, connectionStatus, now, lastFrameAt, copy);
        }
    }
}
