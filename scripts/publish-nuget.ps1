<#
.SYNOPSIS
  Packs SocialPoster.Posting, SocialPoster.Core and SocialPoster.Components and pushes them to NuGet.
  Posting is packed (and therefore pushed) first because Core depends on it.
.PARAMETER ApiKey
  NuGet API key. Defaults to the NUGET_API_KEY environment variable.
.PARAMETER Source
  NuGet feed URL.
.PARAMETER SkipPush
  Only build, test and pack; do not push.
#>
[CmdletBinding()]
param(
    [string]$ApiKey = $env:NUGET_API_KEY,
    [string]$Source = 'https://api.nuget.org/v3/index.json',
    [switch]$SkipPush
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'

if (-not $SkipPush -and -not $ApiKey) {
    throw 'No API key. Pass -ApiKey or set NUGET_API_KEY (or use -SkipPush).'
}

function Invoke-Dotnet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($args -join ' ') failed with exit code $LASTEXITCODE" }
}

if (Test-Path $artifacts) { Remove-Item (Join-Path $artifacts '*.nupkg') -Force -ErrorAction SilentlyContinue }

Invoke-Dotnet test (Join-Path $root 'src/SocialPoster.slnx') --configuration Release
# Dependencies first: Core depends on Posting, Components on Core.
$projects = 'SocialPoster.Posting', 'SocialPoster.Core', 'SocialPoster.Components'
foreach ($project in $projects) {
    Invoke-Dotnet pack (Join-Path $root "src/$project/$project.csproj") --configuration Release --output $artifacts
}

if ($SkipPush) {
    Write-Host "Packed to $artifacts (push skipped)."
    return
}

foreach ($project in $projects) {
    Get-ChildItem $artifacts -Filter "$project.[0-9]*.nupkg" | ForEach-Object {
        Invoke-Dotnet nuget push $_.FullName --api-key $ApiKey --source $Source --skip-duplicate
    }
}
