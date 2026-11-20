# Star Citizen Telemetry Gateway

A read-only Windows tray application that forwards selected, aggregated local
Star Citizen telemetry to a configurable WebSocket receiver.

**Independent community project.** Not affiliated with, endorsed by, sponsored
by, or supported by Cloud Imperium Games or Roberts Space Industries.
Star Citizen and related marks belong to their respective owners.

## Status

Initial implementation in progress. The protocol is build-specific, recovered
from PTU `5.0.202.205` (changelist `13238477`), not an official or stable SDK.
Compatibility with other game builds, game terms, or anti-cheat systems is not
guaranteed. See [NOTICE](NOTICE) for provenance.

## Design

- Native Windows x64 tray application, self-contained .NET 10 distribution.
- Loopback-only, read-only gRPC `TelemetryStream`; no game action RPCs.
- Explicit field allowlist, initially empty. No automatic uploads.
- Five-second aggregation windows and WSS/JSON delivery with batch acknowledgements.
- Provider-neutral endpoint: direct TLS, reverse proxy, or Cloudflare Tunnel.
- Bounded in-memory buffering; no raw gameplay logs or disk telemetry queue.
- API key protected with Windows DPAPI for the current user.

## Development

Install the SDK selected in [global.json](global.json), then:

```sh
dotnet test tests/ScTelemetryGateway.Tests
```

The Windows application requires Windows to run. Linux builds can validate the
core and cross-compile the Windows target, but cannot prove Windows runtime
behavior or live game compatibility.

## License

Project-authored code is licensed under [Apache-2.0](LICENSE).
Required third-party notices and protocol provenance are preserved in [NOTICE](NOTICE).
The license does not grant trademark rights or rights to third-party game assets.
