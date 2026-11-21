# Receiver protocol v1

The receiver is **not included**. Any implementation can accept this protocol,
directly or through a WebSocket-capable reverse proxy. Cloudflare is optional.
The [JSON Schema](telemetry.schema.json) is the normative message shape.

## Connection and authentication

The user configures a full `wss://` URL, for example
`wss://telemetry.example.org/v1/telemetry/stream`. The path is not hardcoded.
Credentials, query parameters and fragments are not accepted in endpoint URLs.
The native client sends `Authorization: Bearer <api-key>` on the HTTP Upgrade.
The receiver must authenticate **before** accepting the WebSocket. Use 401/403
to reject authentication/authorization. API keys must be revocable.

No browser login, JavaScript challenge, provider cookie, Cloudflare SDK,
SignalR negotiation, HTTP fallback, or Access service token is required/supported.
TLS certificate validation is never disabled. Redirects are rejected.

## Messages

Each UTF-8 text message contains one JSON object. Client batches are at most
1 MiB; receiver control messages are at most 16 KiB, including all fragments.
Both peers must handle WebSocket fragmentation. Binary messages are not used.

```json
{
  "type": "telemetry.batch",
  "schema_version": 1,
  "app_version": "0.1.0",
  "session_id": "00000000-0000-4000-8000-000000000001",
  "stream_id": "00000000-0000-4000-8000-000000000002",
  "batch_id": "00000000-0000-4000-8000-000000000003",
  "sequence": 1,
  "window_started_at": "2026-01-01T00:00:00Z",
  "window_ended_at": "2026-01-01T00:00:05Z",
  "frames_received": 2,
  "snapshot": {
    "vehicle.state.flight.instruments.speed": {
      "value": 20,
      "updated_at": "2026-01-01T00:00:04Z",
      "last_known": false
    }
  },
  "statistics": {
    "vehicle.state.flight.instruments.speed": {
      "count": 2, "min": 0, "max": 20, "mean": 10, "last": 20,
      "invalid_count": 0
    }
  },
  "events": [],
  "loss": { "batches_dropped": 0, "events_dropped": 0 }
}
```

This example is synthetic. Field paths use protobuf snake_case names, without
list indices. Snapshot values are flattened **leaves**, not whole unfiltered
protobuf objects. Repeated leaves are arrays in source order; optional absent
list values can be null. Names are not implicitly selected alongside list values.
Only explicitly selected, known paths are sent. New/unknown fields are denied.

Timestamps are local UTC receive/window times, **not server simulation ticks**.
The collection schedule is monotonic; wall-clock corrections may affect UTC
timestamps. A missing section retains its last snapshot with its original
`updated_at`; `last_known` marks disconnection or known inactive vehicle state.
Even `last_known: false` is not a freshness guarantee: always inspect `updated_at`.
A newly received section replaces its previous snapshot, including absent fields.
No prior stream snapshot survives a new game connection.

Statistics cover only new valid samples in this window. `mean` is sample-weighted,
not time-weighted. No invented units or scaling. Absent optional values do not
become zero; protobuf scalars without presence can legitimately be zero/false.
NaN, infinity and float32 maximum sentinel become null and increase
`invalid_count`. An all-invalid statistic has count zero and null aggregates.
Enums use descriptor names (unknown values use numeric strings). int64/uint64
are decimal strings. Enums, booleans, int64, heading and repeated fields do not
receive numeric statistics. Repeated values remain snapshots.

Events have local monotonic `sequence`, `received_at` and `type`; source duplicates
are retained because the source has no event identity. At most 1024 events are
retained per window, dropping oldest on overflow. Loss counters are cumulative
within this collection run. Batch loss is captured when the batch is sealed;
loss caused by later enqueue/transmission appears in a subsequent batch, if any.
Metadata counts received frames, including frames without selected data; it
does not reveal their unselected field values.

## Acknowledgement and replay

After accepting the **whole** batch under its documented persistence/queueing
guarantee, the receiver sends:

```json
{
  "type": "telemetry.ack",
  "schema_version": 1,
  "batch_id": "00000000-0000-4000-8000-000000000003"
}
```

Only one batch is in flight. ACK timeout is 30 seconds; the connection is
aborted and retried on timeout. A completed socket send or HTTP 101 is not an ACK.
Wrong/duplicate/out-of-order ACKs are protocol errors, not permission to remove
queued data. The receiver must not emit unsolicited or duplicate ACKs.

After reconnect the same batch ID and exact serialized payload are replayed.
The receiver must deduplicate atomically by API principal and batch ID, including
across backend instances and server restarts, for its entire replay retention
period. ACK lost after commit must result in the same ACK on replay, without
reapplying the data. ACK means accepted, not that downstream analytics completed.
There is **no exactly-once delivery guarantee**: bounded RAM, exit, pause and
configuration changes can discard unacknowledged batches.

The queue holds at most 60 batches and 10 MiB serialized bytes, including the
reserved in-flight batch. Overflow evicts oldest waiting batches, never the
in-flight batch. A batch over 1 MiB is discarded and counted. Backlog is paced
to at most one send per configured interval, including after reconnect.

## Errors and liveness

The server can reply to the pending batch with:

```json
{
  "type": "telemetry.error",
  "schema_version": 1,
  "code": "rate_limited",
  "retry_after_seconds": 30
}
```

`rate_limited` delays reconnect; delay must be 1..86400 seconds. Other application
error codes stop collection/delivery and require user intervention. Error text
from the receiver is not displayed or logged. Unexpected message types, schema
versions, binary or oversized replies are fatal protocol errors.

Handshake 408/429/5xx, network faults, ordinary connection closes and ACK timeouts
retry with exponential backoff and jitter (base 1..60 seconds). Handshake
`Retry-After` seconds or HTTP dates are respected, capped at one day.
Other 3xx/4xx, including 401/403, stop delivery with a visible error; 403 may also
mean a proxy/WAF policy, not only an incorrect key. Close codes 1003/1007/1008/1009
stop delivery (as does protocol close 1002). No tight reconnect loop or automatic downgrade to plaintext.

The client uses WebSocket ping/pong (20-second interval, 10-second pong timeout).
This is transport liveness, not an empty telemetry batch or a remote-command
channel. The server must answer control frames. No game-control messages exist.
All telemetry fields start disabled; initial upload requires explicit Start.

## Local MFD protocol

The optional phone/tablet MFD uses a separate unencrypted HTTP and WebSocket
interface on an explicitly selected LAN IPv4 address. It is off by default and
does not use the remote `telemetry.batch` protocol or API key.

MFD settings have an independent field allowlist. Static browser assets are
bundled with the Windows application. No CDN, analytics, external fonts or
runtime network downloads are used.

The browser receives `mfd.snapshot` schema v1 text messages. Each message is a
full replaceable snapshot and is published at a target cadence of 4 Hz
(250 ms). The message contains `session_id`, `stream_id`, `sequence`,
`connection_status`, `published_at`, `last_frame_at`, and the selected
`fields`.

Field entries contain `value`, `updated_at`, and `last_known`. Invalid numbers
are represented as unavailable (`null`) values. Missing fields remain absent
from an otherwise complete snapshot. A stream change or pause clears the
snapshot, and disconnection marks retained values as `last_known`. The browser
marks data stale after 2 seconds and labels disconnected and paused separately.

HTTP and WebSocket access are not confidential or authenticated by TLS.
Untrusted LAN clients must not be able to reach the interface. The gateway
requires explicit pairing and approval. It supports at most 8 active approved
browser sessions. Pairing codes expire after 2 minutes and credentials are
held only in running-service memory and browser session storage.
