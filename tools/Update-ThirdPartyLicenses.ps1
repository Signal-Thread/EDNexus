<#
.SYNOPSIS
  Regenerates the NuGet half of src/EDNexus.App/Assets/third-party-licenses.json.

.DESCRIPTION
  Reads obj/project.assets.json of the desktop app (run `dotnet restore` first), looks each resolved
  package up in the local NuGet cache and records its licence from the .nuspec. Entries whose
  "kind" is not "package" (data sources, bundled fonts, native engines) are hand-maintained and are
  kept as they are. Packages are never deleted silently: review the diff.

  Packages whose nuspec carries only a licenseUrl (no SPDX expression) are written with licence
  "UNKNOWN" so they cannot slip through; fix them by hand in "overrides" below.
#>
[CmdletBinding()]
param(
  [string]$Assets = (Join-Path $PSScriptRoot '..\src\EDNexus.App\obj\project.assets.json'),
  [string]$Out = (Join-Path $PSScriptRoot '..\src\EDNexus.App\Assets\third-party-licenses.json')
)

$ErrorActionPreference = 'Stop'

# Fill in where a nuspec has no SPDX expression. Key = package id (lower case).
$overrides = @{ 'avalonia.angle.windows.natives' = 'BSD-3-Clause' }  # Google ANGLE; the nuspec only points at a LICENSE file

$doc = Get-Content $Assets -Raw | ConvertFrom-Json
$direct = @{}
foreach ($prop in $doc.projectFileDependencyGroups.PSObject.Properties) {
  foreach ($d in $prop.Value) { $direct[($d -split ' ')[0].ToLowerInvariant()] = $true }
}
$root = $doc.packageFolders.PSObject.Properties.Name | Select-Object -First 1

$existing = @()
if (Test-Path $Out) {
  $existing = @((Get-Content $Out -Raw | ConvertFrom-Json).entries | Where-Object { $_.kind -ne 'package' })
}

$packages = foreach ($lib in $doc.libraries.PSObject.Properties) {
  $spec = $lib.Value
  if ($spec.type -ne 'package') { continue }
  $id, $ver = $lib.Name -split '/'
  $nuspec = Join-Path $root "$($id.ToLowerInvariant())/$ver/$($id.ToLowerInvariant()).nuspec"
  $license = 'UNKNOWN'; $url = $null; $authors = $null
  if (Test-Path $nuspec) {
    [xml]$x = Get-Content $nuspec -Raw
    $m = $x.package.metadata
    $authors = $m.authors
    $url = if ($m.projectUrl) { $m.projectUrl } else { $m.repository.url }
    if ($m.license -and $m.license.type -eq 'expression') { $license = $m.license.'#text' }
    elseif ($m.license) { $license = "SEE FILE: $($m.license.'#text')" }
  }
  if ($overrides.ContainsKey($id.ToLowerInvariant())) { $license = $overrides[$id.ToLowerInvariant()] }
  [ordered]@{
    kind    = 'package'
    name    = $id
    version = $ver
    license = $license
    url     = $url
    authors = $authors
    direct  = [bool]$direct.ContainsKey($id.ToLowerInvariant())
  }
}

$all = @($packages | Sort-Object { $_.name.ToLowerInvariant() }) + $existing
[ordered]@{ generated = (Get-Date -Format 'yyyy-MM-dd'); entries = $all } |
  ConvertTo-Json -Depth 5 | Set-Content $Out -Encoding utf8
"Wrote $($packages.Count) packages (+$($existing.Count) hand-maintained) to $Out"


