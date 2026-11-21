# Star Citizen Telemetry Gateway

A read-only Windows tray application that forwards selected, aggregated local
Star Citizen telemetry to a configurable WebSocket receiver.

**Independent community project.** Not affiliated with, endorsed by, sponsored
by, or supported by Cloud Imperium Games.
Star Citizen and related marks belong to their respective owners.

## Compatibility

The protocol is build-specific, recovered from PTU `5.0.202.205`
(changelist `13238477`), not an official or stable SDK.
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
- Per-user Windows installer and approval-gated built-in updates.

## Windows quick start

1. Download `Setup.exe` from [Releases](https://github.com/zolij/sc-telemetry-gateway/releases),
   install it for your Windows user, then launch it from the Start menu/tray.
   Supported target: Windows 11 x64. No separate .NET runtime is required.
   Current previews are explicitly **unsigned**; Windows may show a
   publisher/SmartScreen warning. Trusted signing requires external approval.
   The ZIP from a successful [Actions run](https://github.com/zolij/sc-telemetry-gateway/actions)
   remains available for portable use; extract all files before running the EXE.
2. In the game's console, configure and enable the local service:

   ```text
   grpc.client.server.port 50051
   grpc.client.server.external 0
   server.game.telemetry.enabled 1
   ```

3. Open **Settings** from the tray icon. Configure the same game port, your
   receiver's full WSS URL and its API key. Select only the fields you need.
4. Choose **Start** explicitly. Closing the status window leaves the tray running.
   Use **Pause and clear buffer** or **Exit** to stop collection and upload.

The app neither starts with Windows nor resumes collection automatically.
An empty field selection never uploads telemetry. Applying settings stops the
current run and clears its window/queue; Start again when ready. Already sent
data cannot be recalled. A protocol/authentication error stops the run and is
shown in Status and a tray notification.

Settings live in `%LOCALAPPDATA%\StarCitizenTelemetryGateway\settings.json`;
the separately DPAPI-encrypted key is `api-key.bin`. Do not share either file.
Loss of the Windows user profile/key material may make the key unrecoverable;
enter it again instead of disabling protection. Corrupt settings are reported,
not silently replaced. No telemetry values are written to disk.

## Local phone/tablet MFD

The bundled MFD is disabled by default. To use it, select **Enable local MFD
access** in Settings, choose an eligible IPv4 address and port, and select an
independent MFD field allowlist. Start collection and open the displayed URL on
a phone or tablet on the same trusted network.

Create a two-minute pairing code in Settings and have the browser submit it with
a device label. The pending request must be explicitly approved in Settings.
Approved devices can be revoked independently. The HTTP and WebSocket interface
is **not encrypted** and may expose telemetry to an observer or active attacker
on the network. It does not use WSS, secure-context-only PWA features or remote
upload credentials. Use it only on a trusted network; configure the Windows
Firewall prompt yourself and do not open router forwarding.

The display has Flight, Systems and Crew views. Each shows key selected values;
expand “More” for other fields in that view. Fields not selected in the MFD
settings are labelled as such, unavailable values are not replaced with zeros,
and stale or last-known data is marked. Values are shown without assumed units
or scales.

Installed copies check GitHub for updates automatically (configurable).
Downloading and installation each require confirmation; downloaded updates
never auto-install on startup. Unsigned preview updates require an explicit
Settings opt-in. See [installation and update privacy](docs/INSTALLATION.md)
and the [code signing policy and setup](docs/SIGNING.md).

## Receiver

Bring your own server implementing [the protocol and ACK contract](docs/PROTOCOL.md).
The app sends selected last-known snapshots, window statistics and buffered
events every 5 seconds by default (configurable from 1 to 300 seconds).
No game actions or arbitrary remote commands are supported.

The [deployment guide](docs/DEPLOYMENT.md) covers direct TLS, reverse proxies
and optional Cloudflare Tunnel. The [JSON Schema](docs/telemetry.schema.json)
and synthetic example describe the wire format independently of server technology.

The RAM queue holds at most 60 batches / 10 MiB. Events are capped at 1024 per
window and each batch at 1 MiB. Overflow, oversize, exit and pause can lose data;
the Status window reports drops during a run. Reconnect retries preserve batch
IDs for server-side deduplication, but do not guarantee lossless delivery.
Forwarding data to a receiver is a separate privacy decision; its operator
controls retention and access after receipt.

## Development

Install the SDK selected in [global.json](global.json), then:

```sh
dotnet test tests/ScTelemetryGateway.Tests
RUN_PROXY_TESTS=1 dotnet test tests/ScTelemetryGateway.Tests
dotnet publish src/ScTelemetryGateway.Windows -c Release -r win-x64 --self-contained true -o artifacts/win-x64
```

The second command requires Docker and runs a temporary Nginx reverse proxy;
the test removes its own container and temporary files. On unusual Docker/WSL
networks, `TEST_PROXY_ORIGIN_HOST` can override the address the container uses to
reach the test host. The default is `host.docker.internal`.

The Windows application requires Windows to run. Linux builds can validate the
core and cross-compile the Windows target, but cannot prove Windows runtime
behavior or live game compatibility. CI runs core/integration tests on Linux and
Windows, the Docker proxy test on Linux, and DPAPI tests and a self-contained
application startup smoke test on Windows. Windows CI also installs Setup,
verifies an actual approved upgrade, confirms downloaded updates do not apply
without approval, preserves settings/the DPAPI key, and uninstalls the app.

Live Star Citizen and an actual Cloudflare Tunnel require the deployment
checklist; passing synthetic tests is not a claim that those live tests ran.
All code comments, documentation and commit messages are written in English.

## License

Project-authored code is licensed under [Apache-2.0](LICENSE).
Required third-party notices and protocol provenance are preserved in [NOTICE](NOTICE).
The license does not grant trademark rights or rights to third-party game assets.
