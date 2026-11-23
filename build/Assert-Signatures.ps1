param(
    [Parameter(Mandatory)][string[]]$Paths,
    [Parameter(Mandatory)][string]$Thumbprint,
    [Parameter(Mandatory)][string]$Version
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'Authenticode verification requires Windows.' }
if ($Thumbprint -notmatch '^[0-9A-Fa-f]{40}$') { throw 'Configure the approved certificate SHA-1 thumbprint.' }
foreach ($path in $Paths) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing signed file: $path" }
    $signature = Get-AuthenticodeSignature -LiteralPath $path
    if ($signature.Status -ne 'Valid' -or
        $signature.SignerCertificate.Thumbprint -ne $Thumbprint -or
        $null -eq $signature.TimeStamperCertificate) {
        throw "Missing, invalid, unapproved or untimestamped signature: $path"
    }
    $metadata = [Diagnostics.FileVersionInfo]::GetVersionInfo((Resolve-Path -LiteralPath $path).Path)
    if ($metadata.ProductName -ne 'Verse Data Gateway' -or
        $metadata.ProductVersion.Split('+')[0] -ne $Version) {
        throw "Unexpected signed product identity or version: $path"
    }
}
