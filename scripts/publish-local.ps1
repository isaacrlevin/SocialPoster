<#
.SYNOPSIS
  Packs SocialPoster.Posting, SocialPoster.Core and SocialPoster.Components into a local NuGet feed folder
  so other apps can consume them before a real release.
.PARAMETER FeedPath
  Folder used as the local feed. Defaults to ~/.nuget-local.
.PARAMETER VersionSuffix
  Prerelease suffix appended to the project's base version. Defaults to 'local.<timestamp>'
  so every run produces a fresh version and consumers never get a stale cached package.
.PARAMETER Register
  Registers FeedPath as a user-level NuGet source named 'SocialPosterLocal' if it is missing.
.PARAMETER SkipTests
  Skip running the test suite before packing.
#>
[CmdletBinding()]
param(
    [string]$FeedPath = (Join-Path $HOME '.nuget-local'),
    [string]$VersionSuffix = "local.$(Get-Date -Format 'yyyyMMddHHmmss')",
    [switch]$Register,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$sourceName = 'SocialPosterLocal'

function Invoke-Dotnet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($args -join ' ') failed with exit code $LASTEXITCODE" }
}

New-Item -ItemType Directory -Force $FeedPath | Out-Null

if (-not $SkipTests) {
    Invoke-Dotnet test (Join-Path $root 'src/SocialPoster.slnx') --configuration Release
}

# Packed in dependency order; every package gets the same version so the dependencies resolve to it.
$baseVersion = ([xml](Get-Content (Join-Path $root 'src/SocialPoster.Components/SocialPoster.Components.csproj'))).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1
$baseVersion = $baseVersion -replace '-.*$', ''
$version = "$baseVersion-$VersionSuffix"

foreach ($project in 'SocialPoster.Posting', 'SocialPoster.Core', 'SocialPoster.Components') {
    Invoke-Dotnet pack (Join-Path $root "src/$project/$project.csproj") --configuration Release --output $FeedPath "-p:Version=$version"
}

if ($Register) {
    $existing = & dotnet nuget list source | Select-String -SimpleMatch $sourceName
    if (-not $existing) {
        Invoke-Dotnet nuget add source $FeedPath --name $sourceName
    }
}

Write-Host ""
Write-Host "Published SocialPoster.Posting, SocialPoster.Core and SocialPoster.Components $version to $FeedPath"
Write-Host "Consume with:  dotnet add package SocialPoster.Components --version $version --source `"$FeedPath`""
if (-not $Register) {
    Write-Host "Or re-run with -Register to add the feed as a user-level NuGet source."
}
