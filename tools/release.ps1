<#
.SYNOPSIS
  Local beta release: Native AOT publish, Velopack pack, upload as a pre-release of adgranger/vigo-installer.
  Same steps as .github/workflows/release.yml, run on this machine instead of a GitHub runner.
.PARAMETER Version
  SemVer, with or without a leading "v" (e.g. 0.14.1 or v0.15.0-beta.1).
.PARAMETER Token
  Token with "Contents: read and write" on the installer repository. Defaults to $env:INSTALLER_REPO_TOKEN.
.PARAMETER SkipTests
  Do not run the unit tests before publishing.
.PARAMETER NoUpload
  Pack only (output in Releases/), do not upload.
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Token = $env:INSTALLER_REPO_TOKEN,
    [switch]$SkipTests,
    [switch]$NoUpload
)

$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)

$installerRepo = 'https://github.com/adgranger/vigo-installer'
$v = $Version.TrimStart('v')
if ($v -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "Invalid version '$v'" }
# The "What's new" dialog shows the section titled with this exact version: none means no dialog.
foreach ($f in 'src/Vigo.Core/Changelog/CHANGELOG-fr.md', 'src/Vigo.Core/Changelog/CHANGELOG-en.md') {
    if (-not (Select-String -Path $f -Pattern "^## $([regex]::Escape($v))( |$)" -Quiet)) { throw "No '## $v' section in $f" }
}
if (-not $NoUpload -and -not $Token) { throw 'Missing token: pass -Token or set INSTALLER_REPO_TOKEN (or use -NoUpload).' }

function Invoke-Native {
    param([string]$Label, [scriptblock]$Command)
    Write-Host "==> $Label" -ForegroundColor Cyan
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$Label failed (exit code $LASTEXITCODE)" }
}

if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    Invoke-Native 'Install vpk' { dotnet tool install --global vpk --version 1.2.161 }
}

if (-not $SkipTests) {
    Invoke-Native 'Tests (Core)' { dotnet test --project tests/Vigo.Core.Tests -p:Platform=x64 }
    Invoke-Native 'Tests (Git)' { dotnet test --project tests/Vigo.Git.Tests -p:Platform=x64 }
}

# Dedicated folders: never touch artifacts/publish/aot-x64 (the user may be running it).
$publishDir = "artifacts/publish/release-$v"
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
Invoke-Native 'Publish (Native AOT)' {
    dotnet publish src/Vigo.App -c Release -r win-x64 -p:Platform=x64 -p:Version=$v -o $publishDir
}

if (-not $NoUpload) {
    # Previous releases give the delta packages; failure is not fatal (first release, offline...).
    Write-Host '==> Download previous releases' -ForegroundColor Cyan
    vpk download github --repoUrl $installerRepo --pre --token $Token
    if ($LASTEXITCODE -ne 0) { Write-Warning 'No previous release downloaded; continuing without deltas.' }
}

Invoke-Native 'Pack' {
    vpk pack --packId VIGO --packTitle 'VIGO' --packAuthors 'VIGO' --packVersion $v `
        --packDir $publishDir --mainExe Vigo.exe --icon src/Vigo.App/Assets/Vigo.ico --outputDir Releases
}

if ($NoUpload) { Write-Host "Packed in Releases/ (not uploaded)." -ForegroundColor Green; return }

Invoke-Native 'Upload (pre-release)' {
    vpk upload github --repoUrl $installerRepo --outputDir Releases --token $Token `
        --tag "v$v" --releaseName "VIGO $v" --pre --publish
}
Write-Host "Released VIGO $v to $installerRepo" -ForegroundColor Green
