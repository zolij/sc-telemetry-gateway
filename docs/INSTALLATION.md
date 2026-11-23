# Install, update and uninstall

Use the application at your own risk. It is provided "AS IS", without
warranties, and the authors and contributors disclaim liability to the fullest
extent permitted by law. You are responsible for your configuration, network
exposure and use of telemetry. See the full [disclaimer](DISCLAIMER.md); the
Apache-2.0 license and any rights that cannot be excluded by law remain
unaffected.

## Windows installer

Use the `Setup.exe` asset from the project's GitHub Releases page. A preview
release is explicitly labelled **UNSIGNED PREVIEW** until trusted code signing
has been approved. Do not treat a checksum as a publisher signature.

Close any running copy first. Setup installs for the current Windows
user under `%LOCALAPPDATA%\Spaceling.VerseDataGateway`, creates a Start
menu shortcut and an uninstall entry, and does not require elevation. There is
no system service or automatic startup with Windows.

The dashboard opens on launch and the app stays in the notification area when
the window is closed. Double-click the tray icon to reopen the dashboard.
Telemetry collection is always paused on launch, including after an update.
For a local MFD, use Settings to choose a trusted network interface, select MFD
fields and enable local MFD access; then start collection and pair your display
from the dashboard. Paired displays can trigger game-supported actions. Local
traffic is unencrypted and can be observed or modified; use a trusted network.
Remote upload is optional and
has its own field selection. Neither destination starts automatically.

Before installing, Setup explains application usage statistics and offers a
checkbox to disable them. The default choice is enabled. Clear **Send
application usage statistics** to disable them before the first report, or
change the setting later in **Settings > Privacy**. Portable copies, silent
installations and upgrades from versions without the notice ask on first
launch instead; choose **No** to disable reporting. See
[usage statistics](USAGE-STATISTICS.md) for the transmitted fields, Cloudflare
processing and retention. This does not start game telemetry collection.

### Previous installations

The rename changes the installer identity and settings directory. Exit and
uninstall an installation under the previous project name, then install
Verse Data Gateway manually; the previous built-in updater cannot upgrade to
the new package. Previous settings, keys, profiles, and device permissions are
not imported automatically.

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
approved. After restart, collection remains paused. Settings, the DPAPI key,
MFD profiles and approved MFD device records stay in
`%LOCALAPPDATA%\VerseDataGateway`, outside versioned app files.

Stable is the default update channel. **Include unsigned preview updates** is
an explicit opt-in; previews are not stable and may have no trusted signature.
Stable clients never automatically switch to previews.
Stable releases are also advertised to preview clients so that opting into
previews does not hide a newer stable release. The old portable ZIP
does not support managed updates: install Setup to enable them.

## Uninstall

Use Windows **Settings > Apps > Installed apps > Verse Data Gateway >
Uninstall**. Close the tray app first. Uninstall removes program files and
shortcuts, but retains user settings, the encrypted API key, MFD profiles and
approved device records. To remove those too, delete the
`VerseDataGateway` folder under your own LocalAppData after exiting
the app. This also removes the local usage statistics ID. Browser-stored MFD
tokens and identifiers are separate. Previously sent usage records remain on
the service until their scheduled cleanup. No game telemetry archive needs to
be removed.

## Build

On Windows, with the pinned .NET SDK:

```powershell
./build/Pack.ps1 -Version 0.2.0-preview.2
```

The script restores the repository-pinned Velopack tool, publishes a
self-contained Windows x64 app and creates a per-user Setup plus a complete
update package/feed. No paid updater hosting is required; release assets are
hosted on the public GitHub repository.
