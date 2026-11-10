<#
Creates a self-signed code-signing certificate for the Claude Usage package and
prepares the two GitHub secrets the release workflow looks for.

Run:   pwsh tools/New-SigningCert.ps1

With the GitHub CLI (gh, signed in) the secrets are set for you. Without it, the
values are written to files so you can paste them at
https://github.com/<repo>/settings/secrets/actions

The files live in your user folder, NOT the repository. Keep them: signing
future releases with a different certificate means everyone has to trust the
new one again. Running the script again reuses an existing certificate.
#>
param(
    [string]$Subject = 'CN=Claude Usage Widget',
    [string]$Repo = 'Cheaterdev/widbar-claude-usage',
    [string]$OutDir = (Join-Path $env:USERPROFILE 'ClaudeUsage-signing')
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutDir | Out-Null

$pfx = Join-Path $OutDir 'ClaudeUsage.pfx'
$cer = Join-Path $OutDir 'ClaudeUsage.cer'
$pwFile = Join-Path $OutDir 'password.txt'

if ((Test-Path $pfx) -and (Test-Path $pwFile)) {
    Write-Host "Reusing existing certificate in $OutDir"
    $plain = (Get-Content $pwFile -Raw).Trim()
}
else {
    $bytes = New-Object byte[] 24
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $plain = [Convert]::ToBase64String($bytes)
    $password = ConvertTo-SecureString $plain -AsPlainText -Force

    $cert = New-SelfSignedCertificate `
        -Type Custom `
        -Subject $Subject `
        -KeyUsage DigitalSignature `
        -FriendlyName 'Claude Usage Widget' `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -KeyExportPolicy Exportable `
        -NotAfter (Get-Date).AddYears(5) `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')

    Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $password | Out-Null
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null
    Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)"   # keep it out of the local store
    Set-Content -Path $pwFile -Value $plain -Encoding ascii
}

$b64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($pfx))
$b64File = Join-Path $OutDir 'SIGNING_CERT_BASE64.txt'
Set-Content -Path $b64File -Value $b64 -Encoding ascii

Write-Host ''
if (Get-Command gh -ErrorAction SilentlyContinue) {
    $b64 | gh secret set SIGNING_CERT_BASE64 --repo $Repo
    $plain | gh secret set SIGNING_CERT_PASSWORD --repo $Repo
    Write-Host "Secrets SIGNING_CERT_BASE64 and SIGNING_CERT_PASSWORD set on $Repo."
}
else {
    Write-Host 'GitHub CLI not found, so add the two secrets by hand:'
    Write-Host "  https://github.com/$Repo/settings/secrets/actions  >  New repository secret"
    Write-Host "  SIGNING_CERT_BASE64   = contents of $b64File"
    Write-Host "  SIGNING_CERT_PASSWORD = contents of $pwFile"
    Start-Process explorer.exe $OutDir
}

Write-Host ''
Write-Host "Keep private: $pfx and $pwFile"
Write-Host "Public certificate: $cer"
Write-Host 'Then push a new tag (for example v0.1.1) so the workflow builds a signed release.'
