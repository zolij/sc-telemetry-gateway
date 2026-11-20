param(
    [Parameter(Mandatory)][string]$Installer,
    [Parameter(Mandatory)][string]$Version
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'Installer verification requires Windows.' }
$root = Join-Path $env:LOCALAPPDATA 'Spaceling.StarCitizenTelemetryGateway'
$settingsRoot = Join-Path $env:LOCALAPPDATA 'StarCitizenTelemetryGateway'
if ((Test-Path $root) -or (Test-Path $settingsRoot)) {
    throw 'Installer smoke test requires a clean CI profile; it will not overwrite an existing installation or settings.'
}
$exe = Join-Path $root 'current/StarCitizenTelemetryGateway.exe'
$dll = Join-Path $root 'current/StarCitizenTelemetryGateway.dll'
$settings = Join-Path $settingsRoot 'settings.json'
$key = Join-Path $settingsRoot 'api-key.bin'
$oldTestMode = $env:SCTG_INSTALLER_TEST
$env:SCTG_INSTALLER_TEST = '1'
$verified = $false
function Run-App([string[]]$AppArguments) {
    $argumentLine = ($AppArguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    Write-Output "Running installed app: $argumentLine"
    $stdout = Join-Path $env:TEMP 'sctg-installer-test.stdout'
    $stderr = Join-Path $env:TEMP 'sctg-installer-test.stderr'
    $process = Start-Process $exe -ArgumentList $argumentLine -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    if (-not $process.WaitForExit(60000)) {
        $process.Refresh()
        Write-Output "Timed out with window title: $($process.MainWindowTitle)"
        Get-Content $stdout, $stderr
        Stop-Process -Id $process.Id
        throw 'Installed application test timed out.'
    }
    Get-Content $stdout, $stderr
    if ($process.ExitCode -ne 0) { throw "Installed application exited with $($process.ExitCode)." }
}
try {
    if (-not $verified -and (Test-Path $root)) {
        Get-ChildItem $root -Filter '*.log' -Recurse | Select-Object -First 5 | ForEach-Object {
            Write-Output "Installer diagnostic log: $($_.FullName)"
            Get-Content $_.FullName -Tail 40
        }
    }
    $process = Start-Process (Resolve-Path $Installer).Path -ArgumentList '--silent' -PassThru
    if (-not $process.WaitForExit(60000)) {
        Stop-Process -Id $process.Id
        throw 'Installer timed out.'
    }
    if ($process.ExitCode -ne 0) { throw "Installer exited with $($process.ExitCode)." }
    for ($i = 0; $i -lt 100 -and -not (Test-Path $exe); $i++) { Start-Sleep -Milliseconds 200 }
    if (-not (Test-Path $exe)) { throw 'Per-user installation did not create the expected application.' }
    Start-Sleep -Seconds 3
    New-Item -ItemType Directory -Path $settingsRoot -Force | Out-Null
    Set-Content $settings '{"game_port":50051,"endpoint":"","interval_seconds":5,"fields":[],"automatic_update_checks":false,"include_preview_updates":false}'
    $clear = [Text.Encoding]::UTF8.GetBytes('synthetic-installer-smoke-key')
    [IO.File]::WriteAllBytes($key, [Security.Cryptography.ProtectedData]::Protect(
        $clear, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser))
    $settingsHash = (Get-FileHash $settings).Hash
    $keyHash = (Get-FileHash $key).Hash
    $baseline = [Diagnostics.FileVersionInfo]::GetVersionInfo($dll).FileVersion
    Run-App -AppArguments @('--smoke-test')
    $baseVersion = [version]$Version.Split('-')[0]
    $next = "$($baseVersion.Major).$($baseVersion.Minor).$($baseVersion.Build + 1)"
    Write-Output 'Building an unsigned stable-channel package only for local upgrade verification; it must not be published.'
    & ./build/Pack.ps1 -Version $next -Channel win -PublishDirectory artifacts/upgrade-publish -OutputDirectory artifacts/upgrade-feed
    $feed = (Resolve-Path artifacts/upgrade-feed).Path
    Run-App -AppArguments @('--download-update-smoke-test', $feed)
    Run-App -AppArguments @('--smoke-test')
    if ([Diagnostics.FileVersionInfo]::GetVersionInfo($dll).FileVersion -ne $baseline) {
        throw 'A downloaded update was installed on startup without approval.'
    }
    Run-App -AppArguments @('--apply-update-smoke-test', $feed)
    $expected = "$($baseVersion.Major).$($baseVersion.Minor).$($baseVersion.Build + 1).0"
    for ($i = 0; $i -lt 150; $i++) {
        if ((Test-Path $dll) -and [Diagnostics.FileVersionInfo]::GetVersionInfo($dll).FileVersion -eq $expected) { break }
        Start-Sleep -Milliseconds 200
    }
    if ([Diagnostics.FileVersionInfo]::GetVersionInfo($dll).FileVersion -ne $expected) { throw 'Approved update was not applied.' }
    Start-Sleep -Seconds 3
    Run-App -AppArguments @('--smoke-test')
    if ((Get-FileHash $settings).Hash -ne $settingsHash -or (Get-FileHash $key).Hash -ne $keyHash) {
        throw 'Updating changed user configuration or the protected API key.'
    }
    Write-Output 'Verified per-user install, explicit update approval, upgrade, restart and preserved settings/key.'
    $verified = $true
} finally {
    try {
        Get-Process -Name StarCitizenTelemetryGateway -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -eq $exe } |
            ForEach-Object { Stop-Process -Id $_.Id }
        $updater = Join-Path $root 'Update.exe'
        if (Test-Path $updater) {
            $process = Start-Process $updater -ArgumentList 'uninstall --silent' -PassThru
            if (-not $process.WaitForExit(60000)) {
                Stop-Process -Id $process.Id
                throw 'Uninstall timed out.'
            }
            if ($process.ExitCode -ne 0) { throw 'Uninstall failed.' }
        }
        if ($verified -and (-not (Test-Path $settings) -or -not (Test-Path $key) -or
            (Get-FileHash $settings).Hash -ne $settingsHash -or (Get-FileHash $key).Hash -ne $keyHash)) {
            throw 'Uninstall did not preserve user settings and the protected API key.'
        }
    } finally {
        $env:SCTG_INSTALLER_TEST = $oldTestMode
        if (Test-Path $settings) { Remove-Item -LiteralPath $settings }
        if (Test-Path $key) { Remove-Item -LiteralPath $key }
        if ((Test-Path $settingsRoot) -and @(Get-ChildItem -Force $settingsRoot).Count -eq 0) {
            Remove-Item -LiteralPath $settingsRoot
        }
    }
}
if (Test-Path $exe) { throw 'Uninstall left the installed application executable behind.' }
Write-Output 'Verified uninstall.'
