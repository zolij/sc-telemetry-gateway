# Code signing policy

## Current status

Trusted code signing is **not configured or claimed**. Preview releases are
explicitly unsigned. Stable releases are blocked unless trusted signing is
configured and verified. Self-signed certificates are not a free replacement
for a certificate trusted by Windows.

The proposed free provider is [SignPath Foundation](https://signpath.org/).
Its open-source program requires an application and external approval; a new
project is not guaranteed acceptance. The maintainer must submit the
[application](https://signpath.org/apply.html) and comply with the
[program terms](https://signpath.org/terms.html). Signing does not guarantee
SmartScreen reputation or eliminate Windows warnings.

## Intended approval process

The proposed roles for this single-maintainer project are:

- Author and reviewer: [@zolij](https://github.com/zolij). Changes proposed by
  non-committers are reviewed by the maintainer.
- Signing approver: [@zolij](https://github.com/zolij), subject to SignPath
  confirming that these roles can be held by the same person.

The signing approver must separately review each signing request in SignPath;
automated requests must not bypass that approval. Require MFA on GitHub and
signing accounts.
Configure a protected `release` GitHub environment and restrict release
workflow changes and approval rights to trusted maintainers.

After acceptance and before enabling signed releases, update this policy with
the confirmed roles and the provider-required credit:
"Free code signing provided by [SignPath.io](https://about.signpath.io),
certificate by [SignPath Foundation](https://signpath.org)".
This is the proposed credit, not a claim that the project has been accepted.

## Questions for the application

- Confirm whether the single maintainer can hold all proposed roles.
- Confirm whether the Velopack-generated Setup is eligible for signing.
- Confirm that the installer's privacy notice and statistics checkbox satisfy
  the installation notice and disable-option requirements.

## Repository setup after approval

Create a SignPath project linked to this repository's verified GitHub build
provenance and an approval-required signing policy. Configure two ZIP artifact
configurations with explicit PE file paths:

1. Application: `StarCitizenTelemetryGateway.exe`,
   `StarCitizenTelemetryGateway.dll`, `ScTelemetryGateway.Core.dll`.
2. Installer: the single `*Setup.exe` produced by the project, containing the
   Velopack installer. Confirm with SignPath that its provenance and use of the
   Velopack installer are eligible.

Configure SignPath file metadata restrictions to enforce the product name
`Star Citizen Telemetry Gateway` and the same product version for all signed
files in a build.

Do not recursively sign every DLL/EXE in the self-contained publish directory.
The .NET runtime, Google/gRPC dependencies and upstream Velopack updater are
third-party files, not project-authored code. Bundling them does not authorize
using this project's certificate to sign them.

Set the following GitHub `release` environment secret:

- `SIGNPATH_API_TOKEN`: credential authorized only to submit signing requests.

Set these environment variables (GitHub **Variables**, not workflow source):

- `SIGNPATH_ORGANIZATION_ID`
- `SIGNPATH_PROJECT_SLUG`
- `SIGNPATH_SIGNING_POLICY`
- `SIGNPATH_APP_CONFIG`: application ZIP artifact configuration slug.
- `SIGNPATH_SETUP_CONFIG`: installer ZIP artifact configuration slug.
- `SIGNPATH_CERTIFICATE_THUMBPRINT`: approved certificate SHA-1 thumbprint
  (40 hexadecimal characters; used for exact certificate pinning, not file hashing).

Do not paste tokens into issues, source files or command histories. The
Foundation controls its certificate and may require a particular publisher
identity, artifact configuration or additional checks. Validate these settings
with the provider before the first signed release. The signed branch of the
workflow cannot be end-to-end tested without an approved account.

## Release controls

Run **Release installer** manually from `main`. With signing disabled, the
version must be `X.Y.Z-preview.N`; the release is always a GitHub prerelease,
never marked latest, and uses the `preview` feed. With signing enabled, the
version must be stable `X.Y.Z` and uses the `win` feed. Existing release
versions must not be overwritten.

The workflow signs only project-owned binaries first, then packages them in
Velopack and a project-owned installer, then separately signs the final Setup.
It verifies Windows trust, an approved
certificate thumbprint, timestamp, product/version metadata, and the unchanged
signed project binaries inside the full update package. The package is not
modified after Velopack calculates update checksums. Installer checksums are
calculated after final signing. Missing signing configuration or failed checks
stop publication rather than silently falling back to unsigned stable output.

Setup, upgrade, restart, explicit-consent behavior, DPAPI/config preservation
and uninstall are exercised on a clean Windows CI profile before publication.
The signing requests need two separate manual approvals.

## Privacy

Application network activity, usage statistics, retention and available
controls are described in [installation and updates](INSTALLATION.md) and
[usage statistics](USAGE-STATISTICS.md). GitHub and Cloudflare process network
requests under their own privacy policies: [GitHub](https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement)
and [Cloudflare](https://www.cloudflare.com/privacypolicy/).

Signing submits the selected public release binaries, build provenance and
signing-request metadata to the provider, not gameplay data, receiver settings
or API keys. Only enable the integration after agreeing to the provider's terms
and privacy policy. GitHub hosts public release/update assets.

Velopack may write local installer/updater diagnostic logs and package caches
containing versions, paths and operation errors. They are separate from
telemetry; this application does not send telemetry values to the updater or
write a gameplay archive.
