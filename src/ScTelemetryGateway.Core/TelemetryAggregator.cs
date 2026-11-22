using System.Text.Json.Nodes;
using Sc.Game.Telemetry.V1;

namespace ScTelemetryGateway.Core;

public sealed class TelemetryAggregator
{
    public const int MaxEvents = 1024;
    private readonly object gate = new();
    private readonly TelemetryField[] fields;
    private readonly TimeProvider clock;
    private readonly Guid sessionId;
    private Guid streamId = Guid.NewGuid();
    private readonly Dictionary<string, SnapshotValue> snapshot = [];
    private readonly Dictionary<string, Accumulator> statistics = [];
    private readonly Queue<TelemetryEvent> events = new();
    private DateTimeOffset windowStarted;
    private long frames, sequence, eventSequence, eventsDropped;

    public TelemetryAggregator(IEnumerable<string> selected, Guid sessionId, TimeProvider? clock = null)
    {
        this.clock = clock ?? TimeProvider.System;
        this.sessionId = sessionId;
        fields = selected.Select(path => FieldCatalog.All[path]).ToArray();
        windowStarted = this.clock.GetUtcNow();
    }

    public void StartStream()
    {
        lock (gate)
        {
            streamId = Guid.NewGuid();
            snapshot.Clear();
            statistics.Clear();
            events.Clear();
            frames = 0;
            windowStarted = clock.GetUtcNow();
        }
    }

    public void Disconnected()
    {
        lock (gate)
            foreach (var path in snapshot.Keys.ToArray())
                snapshot[path] = snapshot[path] with { LastKnown = true };
    }

    public void Ingest(TelemetryStreamResponse frame)
    {
        lock (gate)
        {
            frames++;
            var now = clock.GetUtcNow();
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
                    if (field.SupportsStatistics && (value is not null || invalid > 0))
                    {
                        if (!statistics.TryGetValue(field.Path, out var accumulator))
                            statistics[field.Path] = accumulator = new();
                        accumulator.Invalid += invalid;
                        if (value is JsonValue scalar && double.TryParse(scalar.ToJsonString(),
                                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number))
                            accumulator.Add(number);
                    }
                }
            }
            if (fields.Any(f => f.Path == "events.events.type") && frame.Events is not null)
            {
                foreach (var item in frame.Events.Events)
                {
                    if (events.Count == MaxEvents)
                    {
                        events.Dequeue();
                        eventsDropped++;
                    }
                    events.Enqueue(new(++eventSequence, now, FieldCatalog.EventType(item)));
                }
            }
            if (frame.Actor?.State?.Locomotion == ActorLocomotionState.OnFoot ||
                frame.Events?.Events.Any(e => e.Type == GameEvent.Types.GameEventType.ActorLeaveSeat) == true)
                foreach (var path in snapshot.Keys.Where(p => p.StartsWith("vehicle.", StringComparison.Ordinal)).ToArray())
                    snapshot[path] = snapshot[path] with { LastKnown = true };
        }
    }

    public TelemetryBatch? CloseWindow(long batchesDropped)
    {
        lock (gate)
        {
            var end = clock.GetUtcNow();
            TelemetryBatch? batch = null;
            if (frames > 0 && fields.Length > 0 && (snapshot.Count > 0 || events.Count > 0 || statistics.Count > 0))
                batch = new(sessionId, streamId, Guid.NewGuid(), ++sequence, windowStarted, end, frames,
                    snapshot.ToDictionary(p => p.Key, p => p.Value with { Value = p.Value.Value?.DeepClone() }),
                    statistics.ToDictionary(p => p.Key, p => p.Value.Finish()), [.. events],
                    new(batchesDropped, eventsDropped));
            statistics.Clear();
            events.Clear();
            frames = 0;
            windowStarted = end;
            return batch;
        }
    }

    private sealed class Accumulator
    {
        private long count;
        private double min, max, mean, last;
        public long Invalid { get; set; }
        public void Add(double value)
        {
            if (count++ == 0) min = max = value;
            min = Math.Min(min, value);
            max = Math.Max(max, value);
            mean += (value - mean) / count;
            last = value;
        }
        public NumericStatistics Finish() => count == 0
            ? new(0, null, null, null, null, Invalid)
            : new(count, min, max, mean, last, Invalid);
    }
}
