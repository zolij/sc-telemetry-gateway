# Network setup and receiver deployment

The separate application usage service is described in
[usage statistics](USAGE-STATISTICS.md). It is not the game telemetry receiver
described below.

## Local MFD network binding

The MFD is an opt-in LAN HTTP and WebSocket listener. It is not an HTTPS or
remote cloud service. The Windows application binds only to the single eligible
IPv4 address and port selected in Settings. It does not change Windows Firewall
rules or open router ports. The default MFD port is `29421`; previously saved
port settings remain unchanged. If the browser cannot connect, allow the
application through the firewall for the appropriate private network profile,
and verify the subnet/VLAN permits the PC and phone/tablet to communicate.

Treat HTTP traffic as observable and modifiable. Pairing codes and session
credentials do not provide transport confidentiality or gateway authentication.
Use only on a trusted Wi-Fi or wired LAN. Close the service or pause collection
when local access is not needed.

## Optional remote receiver

Implement the [v1 protocol](PROTOCOL.md) in your own server. This project ships
the Windows client, not a production receiver. The same client works with a
direct WSS endpoint, reverse proxy, or tunnel; no provider mode is needed.

## Requirements

- Valid TLS certificate for the client-facing `wss://` hostname.
- WebSocket Upgrade and `Authorization` forwarding on the configured path.
- No browser challenge, interactive login or redirect on that path.
- Authenticate before upgrade. Validate every message and enforce size/rate
  limits; a successful handshake is not permission to send unlimited data.
- Proxy idle timeouts must allow the 20-second keepalive and 10-second pong
  timeout, with margin. Configure at least 60 seconds, preferably longer.
- Support messages up to 1 MiB; send control replies no larger than 16 KiB.
- Keep origin access private/restricted. Protect any proxy-to-origin network hop.
- Share durable deduplication between replicas; the client does not depend on
  session affinity or always reconnecting to the same process.
- Respect API key revocation for existing connections, not only new handshakes.

### Direct TLS

Expose your receiver on a TLS-enabled WebSocket listener. Configure the full URL
in the tray settings. There is no fixed hostname or mandatory `/v1` prefix.

### Nginx

Inside an existing TLS `server` block (certificates managed by the operator):

```nginx
location /v1/telemetry/stream {
    proxy_pass http://127.0.0.1:8080;
    proxy_http_version 1.1;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection "upgrade";
    proxy_set_header Authorization $http_authorization;
    proxy_set_header Host $host;
    proxy_read_timeout 90s;
    proxy_send_timeout 90s;
}
```

Restrict this location to the intended API route. Do not copy generic forwarded
headers into authorization decisions without a trusted-proxy policy.
The loopback HTTP origin example assumes Nginx and the receiver share a host.

### Caddy

For an existing receiver bound to loopback:

```caddyfile
telemetry.example.org {
    reverse_proxy /v1/telemetry/stream 127.0.0.1:8080
}
```

Caddy handles WebSocket upgrades and HTTPS for an eligible public hostname.
Manage certificate provisioning, access restrictions and timeouts for your deployment.

### Cloudflare Tunnel (optional)

Publish an HTTP/HTTPS application route to the receiver behind `cloudflared`,
for example `http://localhost:8080`. Use the public hostname's WSS URL in the
client. This is not a raw TCP Tunnel route; users need neither WARP nor a local
`cloudflared` installation.

Keep the API route compatible with non-browser clients. Browser challenges and
interactive Access login cannot be completed by this client. Configure applicable
rules according to your Cloudflare plan; merely having an Authorization header
is **not** an authentication guarantee or a safe blanket bypass rule.
Cloudflare Access service tokens are not supported by this version.

Cloudflare WAF/rate limits inspect the upgrade request, not subsequent WebSocket
messages. The receiver must implement message validation, authentication policy,
size limits and pacing limits. Long-lived connections reduce HTTP request counts,
not bandwidth, processing or concurrency requirements. Cloudflare or tunnel
restarts can close connections; reconnect and replay are expected.

References: [WebSockets](https://developers.cloudflare.com/network/websockets/),
[Tunnel routes](https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/routing-to-tunnel/protocols/).

## Smoke test for your real deployment

1. Verify invalid keys fail before Upgrade and valid keys connect.
2. Select a non-sensitive field, Start, and verify batch/ACK traffic.
3. Keep the connection idle and verify ping/pong through the proxy.
4. Restart the receiver/proxy/tunnel; verify reconnection and replay.
5. Lose an ACK after persisting a batch; verify deduplication on retry.
6. Revoke a key and verify an existing connection loses authorization.
7. Pause or change fields/endpoint; verify no old queued batch is sent afterward.

Local tests use synthetic gRPC and WebSocket servers. The optional Docker Nginx
test checks upgrade/auth forwarding and replay, not your TLS, Cloudflare account,
Tunnel configuration or real Star Citizen build. Do not treat those as verified
until you run this checklist in the actual deployment.
