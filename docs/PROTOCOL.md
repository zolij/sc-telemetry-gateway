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
When local MFD and remote upload are both enabled, permanent remote rejection
clears the remote queue and disables remote ingestion while the MFD continues.

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
`connection_status`, `published_at`, `last_frame_at`, `last_frame_age_ms`,
`selected_fields`, and the available selected `fields`.

Field entries contain `value`, `updated_at`, `last_known`, and `age_ms`. Invalid numbers
are represented as unavailable (`null`) values. Missing fields remain absent
from an otherwise complete snapshot; the `selected_fields` list distinguishes
unselected from selected-but-unavailable fields. Ages are measured using the
server's monotonic clock at publication; the browser adds elapsed monotonic time
since receipt. A stream change or pause clears the
snapshot, and disconnection marks retained values as `last_known`. The browser
marks data stale after 2 seconds and labels disconnected and paused separately.

HTTP and WebSocket access are not confidential or authenticated by TLS.
Untrusted LAN clients must not be able to reach the interface. The gateway
requires explicit pairing and approval. It supports at most 8 active approved
browser sessions; unauthenticated connections also reserve a slot. Fragmented
authentication messages are limited to 512 bytes and a 10-second deadline,
including a failed-authentication close handshake. Pairing request bodies are
limited to 4096 bytes, including chunked requests. Pairing codes are single-use,
expire after 2 minutes, and credentials are held only in running-service memory
and browser session storage. Shutdown cancels pending requests and connections.

Each display has its own sender with at most one replaceable pending snapshot.
A send exceeding 5 seconds disconnects only that display; slow displays cannot
block updates to other clients. Selected event fields contain the values from
the latest received event section, not an accumulated event history.

### Custom MFD profiles

The browser has no built-in layouts. Named, multi-page profiles are separate
from the telemetry snapshot and the gateway's field allowlist. Profile editing
does not change subscriptions, enable fields or send game commands.

The following same-origin HTTP endpoints require
`Authorization: Bearer <session_token>` using the token granted by the existing
pairing flow. Every PC-approved display has profile read/write access; revoking
a device also revokes its profile API access. Tokens are never part of URLs or
profile files. Write requests require the gateway's exact `Origin`, and all
requests require its exact `Host`; foreign origins are rejected without CORS.
Responses use `Cache-Control: no-store`.

| Method | Path | Result |
| --- | --- | --- |
| GET | `/api/mfd/fields` | Enabled-field metadata, independent of whether values have arrived |
| GET | `/api/mfd/profiles` | Array of `{id, name, revision}` summaries |
| GET | `/api/mfd/profiles/{id}` | Full profile and revision ETag |
| POST | `/api/mfd/profiles` | Create a new profile; returns 201 and the saved document |
| PUT | `/api/mfd/profiles/{id}` | Replace an existing profile with revision precondition |
| DELETE | `/api/mfd/profiles/{id}` | Delete with revision precondition; returns 204 |

Field metadata entries contain `path`, `type` (lowercase protobuf scalar type),
`repeated`, and `enum_values`. Only fields enabled in the MFD allowlist are
listed. The existing snapshot format and 4 Hz cadence are unchanged.

Profile GET, POST and PUT responses use a strong revision ETag such as `"2"`.
PUT and DELETE require `If-Match: "2"`; PUT also requires the same profile
`id` and `revision` in the body. Revision checking and persistence are
serialized. An outdated/deleted profile returns 412; a missing precondition
returns 428. No last-writer-wins or automatic overwrite is supported.
The summary list has a content ETag and accepts `If-None-Match`, returning 304
when unchanged. Browsers poll it independently of telemetry, preserve editing
drafts on remote changes, and keep active profile/page selection per browser.

Example saved profile:

```json
{
  "schema_version": 1,
  "id": "d30f41f2-4869-4dc6-8199-b6cffb7ce421",
  "revision": 1,
  "name": "My MFD",
  "pages": [{
    "id": "8beaa358-5dac-4ea5-bb5e-cab25d9d974d",
    "name": "Flight",
    "tiles": [{
      "id": "b5930bd3-c044-4a72-9512-9938af8d0b13",
      "field": "vehicle.state.flight.instruments.speed",
      "label": "Speed",
      "widget": "value",
      "size": "large",
      "decimals": 2,
      "rules": []
    }]
  }]
}
```

`schema_version` must be present and equal to 1. Page and tile identifiers are
unique nonempty UUIDs within a profile; creation/import assigns fresh profile,
page and tile IDs and starts the revision at 1. Names and tile labels contain
1–80 characters without control characters. Profiles have 1–16 pages, each
with 0–64 tiles; at most 32 profiles can be stored.

Widgets are `value` or `status` for scalar fields, and `list` for repeated
fields. Sizes are `normal` (1×1), `wide` (2×1) or `large` (2×2), with responsive
width clamping. Decimal precision is 0–6; int64/uint64 wire strings are not
converted to imprecise JavaScript numbers. A status tile can have at most 32
rules: `{value, label, color}`. Values are exact raw scalar text (e.g. `false`
or an enum name), unique within the tile, with a 128-character limit. Rule
labels have the same 80-character limit; colors are `neutral`, `green`,
`amber`, `red` or `blue`. Unmatched values remain visible; stale/offline
values do not keep a misleading live status color. Lists show values from one
repeated field, without inferring relationships between separate field arrays.

Profiles are limited to 512 KiB serialized JSON; request/import bodies,
including chunked requests, have the same byte limit. The complete versioned
store is limited to 16 MiB. Invalid schema/fields/settings return 400,
oversized bodies 413, count/store capacity limits 422, authentication failures
401, and origin violations 403. Storage failures return 503 and are logged on
the PC. Errors have `{error, message}` bodies. Unknown JSON properties are
rejected; imported documents cannot contain executable HTML, CSS or scripts.
Known fields that are no longer enabled may remain in saved/imported profiles,
but their values are not transmitted and their tiles are marked unselected.

The PC stores `{schema_version: 1, profiles: [...]}` in `mfd-profiles.json`
under its existing application-data directory. Writes use flush and atomic
replacement; success is returned only after persistence. Invalid or unsupported
stores are reported, never silently discarded or replaced with an empty store.
Profiles survive gateway restarts, but pairing credentials remain ephemeral:
devices must pair again after a restart. Profile errors do not disable the
independent telemetry transport.

Browser export produces one profile with `schema_version`, `name`, and
`pages`, omitting the server profile ID, revision and credentials. POST accepts
that format as an import and creates a new profile rather than overwriting one
with the same name. Export/import carries layout only, not telemetry values.
Like telemetry, profiles travel over **unencrypted LAN HTTP** and must only be
used on a trusted network.
