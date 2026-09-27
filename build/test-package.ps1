<#
.SYNOPSIS
  Smoke-test the CycloneDDS.NET NuGet package from a local feed — whether built
  locally (build\pack.ps1) or downloaded from a CI run (the 'nuget-packages'
  artifact).

.DESCRIPTION
  Picks the NEWEST matching package by LastWriteTime (so a cluttered
  artifacts\nuget with many historical builds is fine), then consumes it as a real
  PackageReference via examples\PackageSmokeTest: restore -> run the bundled code
  generator (idlc) -> publish/subscribe round-trip.

.EXAMPLE
  .\build\test-package.ps1
  .\build\test-package.ps1 -FeedDir C:\downloads\nuget-packages
#>
[CmdletBinding()]
param(
    [string]$FeedDir = "artifacts/nuget"
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$Feed = (Resolve-Path $FeedDir).Path

function Get-Newest($pattern) {
    Get-ChildItem -Path (Join-Path $Feed $pattern) -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
}

$rt = Get-Newest 'CycloneDDS.NET.[0-9]*.nupkg'
if (-not $rt) { throw "No CycloneDDS.NET.<version>.nupkg found in $Feed" }
$rtVer = $rt.Name -replace '^CycloneDDS\.NET\.(.+)\.nupkg$', '$1'

Write-Host "============================================================"
Write-Host "  Package smoke test"
Write-Host "  feed:    $Feed"
Write-Host "  package: $($rt.Name)  (version $rtVer)"
Write-Host "============================================================"

Remove-Item -Recurse -Force `
    "$env:USERPROFILE\.nuget\packages\cyclonedds.net" -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force `
    "$RepoRoot\examples\PackageSmokeTest\bin", `
    "$RepoRoot\examples\PackageSmokeTest\obj" -ErrorAction SilentlyContinue

Write-Host "`nRunning examples/PackageSmokeTest against $rtVer ..."
dotnet run --project "$RepoRoot\examples\PackageSmokeTest" -c Release `
    -p:SmokePkgVersion=$rtVer -p:RestoreAdditionalSources=$Feed
if ($LASTEXITCODE -ne 0) { throw "runtime package smoke test FAILED" }
Write-Host "  [+] runtime package smoke test PASSED"
Write-Host "`nAll good."
