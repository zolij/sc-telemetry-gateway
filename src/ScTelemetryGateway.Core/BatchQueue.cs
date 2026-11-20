namespace ScTelemetryGateway.Core;

public sealed record PendingBatch(Guid Id, byte[] Payload);

public sealed class BatchQueue
{
    public const int MaxCount = 60;
    public const int MaxBytes = 10 * 1024 * 1024;
    public const int MaxBatchBytes = 1024 * 1024;
    private readonly object gate = new();
    private readonly LinkedList<PendingBatch> items = [];
    private Guid? reserved;
    private int bytes;
    private long dropped;

    public long Dropped { get { lock (gate) return dropped; } }
    public int Count { get { lock (gate) return items.Count; } }
    public int Bytes { get { lock (gate) return bytes; } }

    public bool Enqueue(TelemetryBatch batch) => Enqueue(new PendingBatch(batch.BatchId, WireJson.Serialize(batch)));

    public bool Enqueue(PendingBatch batch)
    {
        lock (gate)
        {
            if (batch.Payload.Length > MaxBatchBytes)
            {
                dropped++;
                return false;
            }
            while (items.Count >= MaxCount || bytes + batch.Payload.Length > MaxBytes)
            {
                var victim = items.First;
                if (victim is not null && victim.Value.Id == reserved) victim = victim.Next;
                if (victim is null)
                {
                    dropped++;
                    return false;
                }
                bytes -= victim.Value.Payload.Length;
                items.Remove(victim);
                dropped++;
            }
            items.AddLast(batch);
            bytes += batch.Payload.Length;
            return true;
        }
    }

    public PendingBatch? Reserve()
    {
        lock (gate)
        {
            reserved = items.First?.Value.Id;
            return items.First?.Value;
        }
    }

    public void Acknowledge(Guid id)
    {
        lock (gate)
        {
            if (reserved != id || items.First?.Value.Id != id)
                throw new InvalidDataException("Unexpected acknowledgement.");
            bytes -= items.First.Value.Payload.Length;
            items.RemoveFirst();
            reserved = null;
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            items.Clear();
            reserved = null;
            bytes = 0;
        }
    }
}
