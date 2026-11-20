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
dotnet vpk pack --packId Zolij.StarCitizenTelemetryGateway --packVersion $Version `
    --packDir $PublishDirectory --outputDir $OutputDirectory --channel $Channel `
    --packTitle 'Star Citizen Telemetry Gateway' --packAuthors 'Star Citizen Telemetry Gateway contributors' `
    --mainExe StarCitizenTelemetryGateway.exe --instLocation PerUser --shortcuts StartMenuRoot `
    --instLicense LICENSE --instReadme docs/INSTALLATION.md --noPortable --delta None --skip-updates
if ($LASTEXITCODE -ne 0) { throw 'Installer packaging failed.' }
if ($Channel -eq 'win') {
    Copy-Item (Join-Path $OutputDirectory 'releases.win.json') (Join-Path $OutputDirectory 'releases.preview.json') -Force
}
