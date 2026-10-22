<#
.SYNOPSIS
  Tests and packs SocialPoster.Posting, SocialPoster.Core and SocialPoster.Components locally.
  Publishing to NuGet happens in CI via Trusted Publishing: push a v* tag (e.g. v0.5.1).
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'

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

Write-Host "Packed to $artifacts. To publish, push a v* tag; CI pushes to NuGet via Trusted Publishing."
