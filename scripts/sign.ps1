#Requires -Version 7
<#
.SYNOPSIS
    Signs the built NuGet packages.
.DESCRIPTION
    Only runs when CODE_SIGNING_ENABLED is set on the repository, because there is no certificate
    yet. It exists so that turning that variable on either works or explains exactly what is
    missing, rather than failing on a script that was never written.

    Everything here reads from the environment. A certificate or its password must never appear
    in this file, in a workflow, or in a log.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Dir
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$packages = @(Get-ChildItem -Path $Dir -Filter '*.nupkg' -File)
if ($packages.Count -eq 0) {
    throw "No .nupkg found in '$Dir'. Nothing to sign."
}

# Fail on the missing prerequisite by name. A signing step that dies with an opaque error during
# a release is worse than one that says which secret is absent.
$certBase64 = $env:CODE_SIGNING_CERT_BASE64
$certPassword = $env:CODE_SIGNING_CERT_PASSWORD
$timestampUrl = if ($env:CODE_SIGNING_TIMESTAMP_URL) { $env:CODE_SIGNING_TIMESTAMP_URL } else { 'http://timestamp.digicert.com' }

$missing = @()
if (-not $certBase64) { $missing += 'CODE_SIGNING_CERT_BASE64' }
if (-not $certPassword) { $missing += 'CODE_SIGNING_CERT_PASSWORD' }

if ($missing.Count -gt 0) {
    throw @"
Code signing is enabled but these secrets are not set in the production environment:
  $($missing -join ', ')

Either add them, or unset the CODE_SIGNING_ENABLED repository variable. An unsigned package is
fine. A release that claims to be signed and is not is not.
"@
}

$certPath = Join-Path ([System.IO.Path]::GetTempPath()) "signing-$([Guid]::NewGuid().ToString('N')).pfx"

try {
    [System.IO.File]::WriteAllBytes($certPath, [Convert]::FromBase64String($certBase64))

    foreach ($package in $packages) {
        Write-Output "signing $($package.Name)"
        dotnet nuget sign $package.FullName `
            --certificate-path $certPath `
            --certificate-password $certPassword `
            --timestamper $timestampUrl `
            --overwrite
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet nuget sign failed for $($package.Name) with exit code $LASTEXITCODE"
        }
    }

    # Verifying is not optional. Signing that silently produced nothing looks identical to
    # signing that worked, right up until a consumer checks.
    foreach ($package in $packages) {
        dotnet nuget verify $package.FullName --all
        if ($LASTEXITCODE -ne 0) {
            throw "signature verification failed for $($package.Name)"
        }
    }

    Write-Output "signed and verified $($packages.Count) package(s)"
}
finally {
    if (Test-Path $certPath) {
        Remove-Item $certPath -Force
    }
}
