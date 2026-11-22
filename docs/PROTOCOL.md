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
`selected_fields`, `events`, `page_events`, `events_dropped`, and the available
selected `fields`.

Field entries contain `value`, `updated_at`, `last_known`, and `age_ms`. Invalid numbers
are represented as unavailable (`null`) values. Missing fields remain absent
from an otherwise complete snapshot; the `selected_fields` list distinguishes
unselected from selected-but-unavailable fields. The MFD checks numeric field
presence in the original protobuf payload: omitted numbers are unknown, not
zero, including numeric leaves inside repeated messages and 64-bit integers.
An explicitly encoded zero remains zero. Proto3 producers can omit genuine
zero values too; those cannot be distinguished from unavailable exports.
Boolean defaults remain `false`; remote batches retain their existing protobuf
default semantics. Ages are measured using the
server's monotonic clock at publication; the browser adds elapsed monotonic time
since receipt. A stream change or pause clears the
snapshot, and disconnection marks retained values as `last_known`. The browser
marks data stale after 2 seconds and labels disconnected and paused separately.

HTTP and WebSocket access are not confidential or authenticated by TLS.
Untrusted LAN clients must not be able to reach the interface. The gateway
requires explicit pairing: a valid one-use code immediately authorizes the
display without a separate PC approval. It supports at most 8 active approved
browser sessions; unauthenticated connections also reserve a slot. Fragmented
authentication messages are limited to 512 bytes and a 10-second deadline,
including a failed-authentication close handshake. Pairing request bodies are
limited to 4096 bytes, including chunked requests. Pairing codes are single-use
and expire after 2 minutes. The dashboard's QR code contains the MFD URL with
the code in its fragment; the browser removes the fragment and sends the code
to the pairing endpoint. A browser-generated identifier is included as the
display label. The browser stores its token and identifier in local storage,
scoped to the gateway origin; the PC persists approved device records and
SHA-256 token hashes in `mfd-devices.json`. Grants survive collection and
application restarts until revoked; revocation disconnects active sockets.
These mechanisms do not protect the unencrypted connection.

Each display has its own sender with at most one replaceable pending snapshot.
A send exceeding 5 seconds disconnects only that display; slow displays cannot
block updates to other clients. When selected, `events.events.type` contains
the latest 100 events in the current game stream, oldest first, including
duplicates. Empty event sections do not erase history. Its field timestamp
tracks the last event update; the browser labels this tile as history rather
than stale live data. `events_dropped` counts older events removed by this cap,
and the browser displays overflow warnings. Stream restarts and pauses clear
both the history and counter; disconnection retains them as last-known data.
The additive `events` array exposes the same retained history as
`{sequence, received_at, type}` records. Event sequence numbers increase for
each occurrence, including identical event types, and reset with `stream_id`.
They remain stable across repeated snapshots. If `events.events.type` is not
selected, this array is empty and event data is not exposed.
`page_events` contains the most recent occurrence of each known nonzero game
event type, ordered by event sequence. This small, schema-bounded list survives
history overflow, so page triggers do not depend on the 100-event history cap.
It follows the same selection, stream-reset and pause rules as `events`.

### Custom MFD profiles

Pages may contain an optional `trigger_events` array of known game-event names;
old profiles without it remain manual pages. Multiple events can target one
page, but an event may appear only once within a profile. Different profiles
can reuse the same events. Unknown, unspecified, null and duplicate trigger
configurations are rejected. Triggers persist and travel with profile
imports/exports, independently of telemetry field selection.

`GET /api/mfd/events` returns the known, nonzero protobuf game-event names to
approved displays, even when event telemetry is not selected. It uses the same
authentication and origin checks as the profile API and does not subscribe to
data or expose game-control RPCs.

Browsers apply page triggers only to the active saved profile. They baseline
the `page_events` occurrences on each WebSocket connection, then consume each new
per-stream event sequence once. A new game stream resets the cursor; initial
events in that new stream can trigger a page when the display is already
connected. Events consumed while editing, busy, inactive or disconnected do
not replay when switching resumes. If multiple new events match, the last
matching event chooses the page. Manual selection does not reset the cursor.
The editor requests confirmation before moving an assigned event to another
page, and cancelling leaves both pages unchanged. Moves are draft changes
until saved; no game commands or subscription changes are sent.

The browser has no built-in layouts. Named, multi-page profiles are separate
from the telemetry snapshot and the gateway's field allowlist. Profile editing
does not change subscriptions, enable fields or send game commands.

The following same-origin HTTP endpoints require
`Authorization: Bearer <session_token>` using the token granted by the existing
pairing flow. Every paired display has profile read/write access; revoking
a device also revokes its profile API access. Tokens are never part of URLs or
profile files. Write requests require the gateway's exact `Origin`, and all
requests require its exact `Host`; foreign origins are rejected without CORS.
Responses use `Cache-Control: no-store`.

| Method | Path | Result |
| --- | --- | --- |
| GET | `/api/mfd/fields` | Enabled-field metadata, independent of whether values have arrived |
| GET | `/api/mfd/events` | Known nonzero game-event names for page triggers, independent of field selection |
| GET | `/api/mfd/profiles` | Array of `{id, name, revision}` summaries |
| GET | `/api/mfd/profiles/{id}` | Full profile and revision ETag |
| POST | `/api/mfd/profiles` | Create a new profile; returns 201 and the saved document |
| PUT | `/api/mfd/profiles/{id}` | Replace an existing profile with revision precondition |
| DELETE | `/api/mfd/profiles/{id}` | Delete with revision precondition; returns 204 |

Field metadata entries contain `path`, `type` (lowercase protobuf scalar type),
`repeated`, `enum_values`, `numeric`, `display_format`, and `maximum_field`.
`numeric` identifies scalar types that can be safely percentage-formatted;
int64/uint64 wire strings are excluded. `maximum_field` identifies a gauge's
companion maximum, if applicable; it does not enable that field.
Only fields enabled in the MFD allowlist are
listed. Existing field-value entries and the 4 Hz snapshot cadence are unchanged.

Profile GET, POST and PUT responses use a strong revision ETag such as `"2"`.
PUT and DELETE require `If-Match: "2"`; PUT also requires the same profile
`id` and `revision` in the body. Revision checking and persistence are
serialized. The final saved document, including its incremented revision, must
also fit the 512 KiB profile limit; rejection leaves the previous file intact.
An outdated/deleted profile returns 412; a missing precondition
returns 428. No last-writer-wins or automatic overwrite is supported.
The summary list has a content ETag and accepts `If-None-Match`, returning 304
when unchanged. A 304 confirms only the cached summaries, not that the active
profile revision has been applied. Browsers reconcile that revision on every
poll, retrying failed or superseded profile fetches even when the list is unchanged.
Browsers poll it independently of telemetry, preserve editing
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
    "trigger_events": ["GAME_EVENT_TYPE_ACTOR_ENTER_SEAT"],
    "tiles": [{
      "id": "b5930bd3-c044-4a72-9512-9938af8d0b13",
      "field": "vehicle.state.flight.instruments.speed",
      "label": "Speed",
      "widget": "value",
      "size": "large",
      "decimals": 2,
      "format": "auto",
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

The optional tile `format` defaults to `auto`, including when reading older
profiles/imports. Supported values are `auto`, `number` (raw override),
`percent_fraction` (0–1 ratio), `percent` (0–100 value) and `percent_max`
(`current / max`, for non-repeated gauges only). Explicit percentage formats
require a supported numeric field. The selected decimal precision applies
to the displayed percentage, not the raw fraction.

Automatic field formats are:

| Field | Interpretation |
| --- | --- |
| `vehicle.state.shields.level`, `vehicle.state.shields.faces` | 0–1 ratios |
| `vehicle.state.flight.instruments.hydrogen_fuel` | 0–1 ratio |
| `vehicle.state.flight.instruments.quantum_fuel` | 0–100 default; scale unverified in the current sampled PTU feed |
| `actor.vitals.health.current`, `actor.vitals.oxygen.current`, `actor.vitals.hunger.current`, `actor.vitals.thirst.current` | Current divided by the corresponding `.max` |
| Other fields, including temperature and unverified vehicle health scales | Raw values |

For `percent_max`, both fields must be shared and received. The maximum must
be finite, positive, and have the same `updated_at` and `last_known` status as
the current value. Missing/invalid/mismatched maxima display an explicit
unavailable percentage, never an assumed denominator. Percentages are not
clamped to 0–100, missing values are not replaced with zeros, and status rules
continue to match raw values before display formatting. Neither the snapshot
schema nor the gateway allowlist is changed by a formatting choice.

Only fields identified as enums by the catalogue have human-readable enum
labels applied. String fields retain their exact spelling and capitalization.
An empty string displays `Empty`, distinct from absent/null values (`Unknown`).

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
Profiles and approved device records survive gateway restarts. Profile errors
do not disable the independent telemetry transport.

Browser export produces one profile with `schema_version`, `name`, and
`pages`, omitting the server profile ID, revision and credentials. POST accepts
that format as an import and creates a new profile rather than overwriting one
with the same name. Export/import carries layout only, not telemetry values.
Like telemetry, profiles travel over **unencrypted LAN HTTP** and must only be
used on a trusted network.
