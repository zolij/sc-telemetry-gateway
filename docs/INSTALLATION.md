# Install, update and uninstall

## Windows installer

Use the `Setup.exe` asset from the project's GitHub Releases page. A preview
release is explicitly labelled **UNSIGNED PREVIEW** until trusted code signing
has been approved. Do not treat a checksum as a publisher signature.

Close any running portable copy first. Setup installs for the current Windows
user under `%LOCALAPPDATA%\Spaceling.StarCitizenTelemetryGateway`, creates a Start
menu shortcut and an uninstall entry, and does not require elevation. There is
no system service or automatic startup with Windows.

The dashboard opens on launch and the app stays in the notification area when
the window is closed. Double-click the tray icon to reopen the dashboard.
Telemetry collection is always paused on launch, including after an update.
Configure a destination and allowed fields in Settings, then choose Start
explicitly in the dashboard.

### Upgrading from 0.2.0-preview.1

The first preview used a different package identity. Exit and uninstall that
version before manually installing `0.2.0-preview.2` or later; do not use its
built-in updater for this identity change. Keep the settings directory when
uninstalling: the new installer uses the same settings and DPAPI-protected key.
Subsequent updates retain the `Spaceling.StarCitizenTelemetryGateway` identity.

## Update checks and consent

Installed copies check this project's public GitHub Releases after approximately
one minute, then every 24 hours while running. Disable automatic checks in
Settings, or use **Check for updates** manually. GitHub sees normal request
metadata such as your IP address and the updater's User-Agent. No telemetry
values, receiver URL or telemetry API key are sent to the update service.

Automatic checks only notify. The app asks before downloading, then asks again
before installation/restart. Declining installation leaves the download cached;
it is **not automatically applied** on the next launch. Check again to install.
Downloads are checksum-verified by Velopack over HTTPS. This does not substitute
for trusted Authenticode signing; see [Code signing policy](SIGNING.md).

Updates stop collection and discard unsent RAM data only when installation is
approved. After restart, collection remains paused. Settings and the DPAPI key
stay in `%LOCALAPPDATA%\StarCitizenTelemetryGateway`, outside versioned app files.

Stable is the default update channel. **Include unsigned preview updates** is
an explicit opt-in; previews are not stable and may have no trusted signature.
Stable clients never automatically switch to previews.
Stable releases are also advertised to preview clients so that opting into
previews does not hide a newer stable release. The old portable ZIP
does not support managed updates: install Setup to enable them.

## Uninstall

Use Windows **Settings > Apps > Installed apps > Star Citizen Telemetry Gateway >
Uninstall**. Close the tray app first. Uninstall removes program files and
shortcuts, but retains user settings and the encrypted API key. To remove those
too, delete the `StarCitizenTelemetryGateway` folder under your own LocalAppData
after exiting the app. No telemetry archive needs to be removed.

## Build

On Windows, with the pinned .NET SDK:

```powershell
./build/Pack.ps1 -Version 0.2.0-preview.2
```

The script restores the repository-pinned Velopack tool, publishes a
self-contained Windows x64 app and creates a per-user Setup plus a complete
update package/feed. No paid updater hosting is required; release assets are
hosted on the public GitHub repository.
