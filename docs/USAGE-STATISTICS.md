# Application usage statistics

## What is measured

This feature is separate from game telemetry and the user-configured receiver.
The interactive installer explains the data sent and offers a checkbox, enabled
by default. Portable copies, silent installations and upgrades from versions
without this notice ask on first launch instead, offering Yes (the default)
and No. No report is sent before the choice. The setting is also available in
**Settings > Privacy**.

When enabled, the application sends a randomly generated installation ID, the
application version and whether the user-started collection process is running
to `https://sc-telemetry-usage.zolij.info/v1/activity`. Reports are sent at
startup, when collection starts, and approximately every six hours while the app
is running. A running collection process does not guarantee a game connection.
Smoke and installer tests do not send reports.

No game telemetry, player name, receiver address, API key, or hardware ID is
included in the event. The ID is pseudonymous, not anonymous. Cloudflare
processes requests and can see network metadata including the IP address;
its own logging, security and retention settings must also be considered.
The Worker uses the address for rate limiting, but does not intentionally store
it in D1 or include it in application error logs.

The local ID is kept as `usage-id` alongside the settings under
`%LOCALAPPDATA%\StarCitizenTelemetryGateway`. Portable and installed copies
using the same Windows profile share it. Disabling reporting stops further
reports and removes the local ID; previously received records remain until
scheduled cleanup. Re-enabling creates a new ID. Removing the settings folder
on uninstall also removes the ID.

The service stores one record per installation, UTC day and activity type,
updating it with the most recently reported version and time. Records become
eligible for deletion 30 days after their latest report; an hourly scheduled
cleanup deletes eligible rows. Cleanup delays or failures can extend physical
retention, so operators must monitor failures. The dashboard excludes records
older than its selected 1-, 7- or 30-day rolling window.

Counts are approximate installations, not people. New Windows profiles, deleted
IDs or re-enabling reporting create new IDs. Offline usage, disabled reporting,
the reporting interval, short collection runs, failed requests and clients that
predate the feature can all lead to undercounting. A public endpoint can also
receive forged reports. Within each dashboard window, an installation is
counted once per activity under its most recently reported version for that
activity.

## Operator setup

The service source is in `usage-service/`. Deployment requires a Cloudflare
account, a zone for `zolij.info`, Workers, D1 and Cloudflare Access. The
repository contains placeholders, not deployed credentials or a provisioned
database. The application cannot report until the service is configured and
deployed. Check current Workers and D1 quotas and charges, and separately
review the applicable privacy obligations, Cloudflare terms, logs and retention
settings before deployment. An opt-out default alone does not establish legal
compliance.

1. Install a supported Node.js version, run `npm ci` in `usage-service`, and
   authenticate Wrangler with the intended Cloudflare account. Do not commit
   API tokens or account credentials.
2. Create a D1 database with `npx wrangler d1 create sc-telemetry-usage`. Put
   its database ID into `usage-service/wrangler.toml`, then apply the schema:
   `npx wrangler d1 execute sc-telemetry-usage --remote --file schema.sql`
   from `usage-service`. Protect the database and restrict account access.
3. Configure a Cloudflare Access self-hosted application for the dashboard
   paths `/dashboard` and `/dashboard/*` on the usage hostname. Use an allow
   policy limited to the intended administrators, with no bypass policy.
   Record its team domain and application audience in the Worker variables
   `ACCESS_TEAM_DOMAIN` and `ACCESS_AUDIENCE`. Do not protect `/v1/activity`
   with interactive Access: the application cannot complete that login.
4. Deploy with `npx wrangler deploy` from `usage-service`. The custom domain
   must belong to a zone in the account. Configure and monitor the scheduled
   Worker trigger, D1 failures, request volume, rate limits and costs. The
   per-IP Worker rate limiter is approximate and per Cloudflare location; it
   does not prevent distributed abuse or guarantee authentic statistics.
5. Verify `/dashboard` and `/dashboard/api` are inaccessible without Access,
   work for an authorized user, and reject a wrong-audience or expired JWT.
   Both endpoints independently validate Access JWT signature, issuer,
   audience and expiration against Cloudflare's public keys. Verify
   `/v1/activity` accepts only valid minimal payloads and that scheduled
   cleanup runs. Do not use real user IDs for smoke tests.

The dashboard reports rolling 1-, 7- and 30-day totals and version breakdowns
for application and collection activity. It uses D1 directly; it does not
publish results or expose a public query endpoint.

For local checks, run `npm test` in `usage-service` and
`npx wrangler deploy --dry-run` after installing dependencies. Tests use an
in-memory database and generated Access signing keys, not live user data.
