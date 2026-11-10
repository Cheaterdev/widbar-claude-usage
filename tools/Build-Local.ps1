<#
Builds a signed, installable package on this computer, without GitHub.
Uses the certificate made by New-SigningCert.ps1 (in %USERPROFILE%\ClaudeUsage-signing).

    pwsh tools/Build-Local.ps1                 # x64 only, fast
    pwsh tools/Build-Local.ps1 -Arm64          # x64 + ARM64 bundle

Result: dist-local\ClaudeUsage-<version>.msixbundle (or .msix) and ClaudeUsage.cer.
Send both to the other computer (for example: tailscale file cp dist-local\* other-pc:).

The manifest and project file are changed for the build and put back afterwards.
#>
param(
    # Use the version of your LAST release: a local build then counts as newer than it
    # (0.1.2.0 < 0.1.2.n) yet older than the next release (0.1.3.0). Windows refuses downgrades.
    [string]$Version = '0.1.2',
    [switch]$Arm64,
    [string]$CertDir = (Join-Path $env:USERPROFILE 'ClaudeUsage-signing')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'ClaudeUsage\ClaudeUsage (Package)\ClaudeUsage (Package).wapproj'
$manifest = Join-Path $root 'ClaudeUsage\ClaudeUsage (Package)\Package.appxmanifest'
$csproj = Join-Path $root 'ClaudeUsage\ClaudeUsage.ExtensionApp\ClaudeUsage.ExtensionApp.csproj'
$pfx = Join-Path $CertDir 'ClaudeUsage.pfx'
$pwFile = Join-Path $CertDir 'password.txt'
$cerOut = Join-Path $CertDir 'ClaudeUsage.cer'
if (-not (Test-Path $pfx) -or -not (Test-Path $pwFile)) { throw "No certificate in $CertDir. Run tools/New-SigningCert.ps1 first." }
$password = (Get-Content $pwFile -Raw).Trim()

# MSBuild from Visual Studio.
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuild = if (Test-Path $vswhere) {
    & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
}
if (-not $msbuild) { $msbuild = (Get-Command msbuild -ErrorAction SilentlyContinue).Source }
if (-not $msbuild) { throw 'MSBuild was not found. Install Visual Studio (with the .NET desktop / Windows App SDK workloads).' }

# The 4th number counts up on every build, so a newer local build installs over an older one.
$minutes = [int]((Get-Date) - [datetime]'2026-01-01').TotalMinutes % 65535
$pkgVersion = "$Version.$minutes"

$cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($pfx, $password)
$subject = [Security.SecurityElement]::Escape($cert.Subject)

# Raw bytes, so the files come back exactly as they were (no BOM or line-ending changes).
$manifestBytes = [IO.File]::ReadAllBytes($manifest)
$csprojBytes = [IO.File]::ReadAllBytes($csproj)
$manifestBackup = [Text.Encoding]::UTF8.GetString($manifestBytes).TrimStart([char]0xFEFF)
$noBom = New-Object Text.UTF8Encoding($false)
try {
    $m = [regex]::Replace($manifestBackup, '(?s)(<Identity\b[^>]*?\bVersion=")[^"]*(")', ('${1}' + $pkgVersion + '${2}'), 1)
    $m = [regex]::Replace($m, '(?s)(<Identity\b[^>]*?\bPublisher=")[^"]*(")', ('${1}' + $subject + '${2}'), 1)
    [IO.File]::WriteAllText($manifest, $m, $noBom)

    # Old output would be picked up below, so clear it.
    $pkgRoot = Split-Path $project -Parent
    Remove-Item (Join-Path $pkgRoot 'AppPackages') -Recurse -Force -ErrorAction SilentlyContinue

    & $msbuild $project -t:Restore -p:Configuration=Release -p:Platform=x64 -v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }

    $msbArgs = @($project, '-t:Build', '-p:Configuration=Release', '-p:Platform=x64',
        '-p:UapAppxPackageBuildMode=SideloadOnly', '-p:AppxAutoIncrementPackageRevision=false',
        '-p:AppxPackageSigningEnabled=true', "-p:PackageCertificateKeyFile=$pfx", "-p:PackageCertificatePassword=$password",
        '-v:minimal')
    if ($Arm64) { $msbArgs += '-p:AppxBundle=Always'; $msbArgs += '-p:AppxBundlePlatforms=x64|arm64' }
    else { $msbArgs += '-p:AppxBundle=Never' }
    & $msbuild @msbArgs
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

    $found = Get-ChildItem (Join-Path $pkgRoot 'AppPackages') -Recurse -File |
        Where-Object { $_.Extension -in '.msixbundle', '.msix' } | Select-Object -First 1
    if (-not $found) { throw 'No .msix or .msixbundle was produced.' }

    $out = Join-Path $root 'dist-local'
    New-Item -ItemType Directory -Force $out | Out-Null
    $name = "ClaudeUsage-$pkgVersion$($found.Extension)"
    Copy-Item $found.FullName (Join-Path $out $name) -Force
    if (Test-Path $cerOut) { Copy-Item $cerOut (Join-Path $out 'ClaudeUsage.cer') -Force }
    else { [IO.File]::WriteAllBytes((Join-Path $out 'ClaudeUsage.cer'), $cert.Export('Cert')) }

    Write-Host ''
    Write-Host "Built $out\$name (package version $pkgVersion)"
    Write-Host 'Send that file and ClaudeUsage.cer to the other computer.'
}
finally {
    [IO.File]::WriteAllBytes($manifest, $manifestBytes)
    [IO.File]::WriteAllBytes($csproj, $csprojBytes)
}
