param(
    [Parameter(Mandatory)][string]$Version,
    [ValidateSet('preview', 'win')][string]$Channel = 'preview',
    [string]$PublishDirectory = 'artifacts/publish',
    [string]$OutputDirectory = 'artifacts/installer',
    [switch]$SkipPublish
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'Windows packaging must run on Windows.' }
if ($Version -notmatch '^\d+\.\d+\.\d+(-preview\.\d+)?$') { throw 'Use X.Y.Z or X.Y.Z-preview.N.' }
if (($Channel -eq 'preview') -ne $Version.Contains('-preview.')) {
    throw 'Preview versions and stable versions must use separate channels.'
}
dotnet tool restore
if ($LASTEXITCODE -ne 0) { throw 'Could not restore the pinned packager.' }
if (-not $SkipPublish) {
    dotnet publish src/ScTelemetryGateway.Windows -c Release -r win-x64 --self-contained true `
        -o $PublishDirectory "-p:Version=$Version" -p:RestoreLockedMode=true
    if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }
}
$application = (Resolve-Path (Join-Path $PublishDirectory 'StarCitizenTelemetryGateway.exe')).Path
if ([Diagnostics.FileVersionInfo]::GetVersionInfo($application).ProductVersion.Split('+')[0] -ne $Version) {
    throw 'Published application version does not match the installer version.'
}
$marker = Join-Path $PublishDirectory 'UNSIGNED-PREVIEW.txt'
if ($Channel -eq 'preview') {
    Set-Content $marker 'UNSIGNED PREVIEW. This build has no trusted publisher signature. It is not a stable release.'
} elseif (Test-Path $marker) {
    Remove-Item -LiteralPath $marker
}
dotnet vpk pack --packId Spaceling.StarCitizenTelemetryGateway --packVersion $Version `
    --packDir $PublishDirectory --outputDir $OutputDirectory --channel $Channel --runtime win-x64 `
    --packTitle 'Star Citizen Telemetry Gateway' --packAuthors 'Star Citizen Telemetry Gateway contributors' `
    --mainExe StarCitizenTelemetryGateway.exe --instLocation PerUser --shortcuts StartMenuRoot `
    --noPortable --delta None --skip-updates
if ($LASTEXITCODE -ne 0) { throw 'Installer packaging failed.' }
$setups = @(Get-ChildItem (Join-Path $OutputDirectory '*Setup.exe'))
if ($setups.Count -ne 1) { throw 'Expected exactly one Velopack installer.' }
dotnet publish src/ScTelemetryGateway.Installer -c Release -r win-x64 `
    -o artifacts/installer-wrapper "-p:Version=$Version" "-p:InstallerPayload=$($setups[0].FullName)" `
    -p:RestoreLockedMode=true
if ($LASTEXITCODE -ne 0) { throw 'Installer wrapper publish failed.' }
Copy-Item artifacts/installer-wrapper/StarCitizenTelemetryGateway.Installer.exe $setups[0].FullName -Force
if ($Channel -eq 'win') {
    Copy-Item (Join-Path $OutputDirectory 'releases.win.json') (Join-Path $OutputDirectory 'releases.preview.json') -Force
}
