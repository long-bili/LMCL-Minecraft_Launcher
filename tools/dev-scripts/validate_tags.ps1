# Validate the Tag tables in DownloadPage against Modrinth's official category list.
# A single wrong letter makes Modrinth return 400, which surfaces in the UI as
# "nothing found" -- very hard to trace back, so check every name mechanically.
$ErrorActionPreference = "Stop"

$src = Get-Content "$PSScriptRoot\..\src\LMCL.App\Views\Pages\DownloadPage.xaml.cs" -Raw

$dictStart = $src.IndexOf("TagsByType = new()")
if ($dictStart -lt 0) { throw "TagsByType definition not found" }
$dict = $src.Substring($dictStart)

$map = @{ "Mod" = "mod"; "ResourcePack" = "resourcepack"; "DataPack" = "datapack"; "Shader" = "shader"; "ModPack" = "modpack" }

$official = Invoke-RestMethod -Uri "https://api.modrinth.com/v2/tag/category" -TimeoutSec 30 -Headers @{ "User-Agent" = "LMCL/2.0.1" }
$valid = @{}
foreach ($c in $official)
{
    if (-not $valid.ContainsKey($c.project_type)) { $valid[$c.project_type] = @{} }
    $valid[$c.project_type][$c.name] = $true
}

$failed = 0
foreach ($key in $map.Keys)
{
    # Split the dictionary on each "[CommunityResourceType." marker and keep the
    # segment for this key up to the next marker. Simpler and more predictable
    # than one cross-table regex.
    $marker = "[CommunityResourceType."
    $at = $dict.IndexOf($marker + $key + "]")
    if ($at -lt 0) { Write-Host "$key => TABLE NOT FOUND"; $failed++; continue }

    $next = $dict.IndexOf($marker, $at + $marker.Length)
    $segment = if ($next -lt 0) { $dict.Substring($at) } else { $dict.Substring($at, $next - $at) }

    $names = [regex]::Matches($segment, '\("([a-z0-9\-]+)"\s*,') | ForEach-Object { $_.Groups[1].Value }
    $type = $map[$key]
    $table = $valid[$type]

    $bad = @($names | Where-Object { -not $table.ContainsKey($_) })
    $dupes = @($names | Group-Object | Where-Object Count -gt 1 | ForEach-Object Name)

    if ($bad.Count -eq 0 -and $dupes.Count -eq 0)
    {
        Write-Host ("{0,-13} => OK  ({1} names, all valid for {2})" -f $key, $names.Count, $type)
    }
    else
    {
        $failed++
        if ($bad.Count -gt 0) { Write-Host ("{0,-13} => INVALID: {1}" -f $key, ($bad -join ", ")) }
        if ($dupes.Count -gt 0) { Write-Host ("{0,-13} => DUPLICATE: {1}" -f $key, ($dupes -join ", ")) }
    }
}

Write-Host ""
if ($failed -eq 0) { Write-Host "ALL PASS"; exit 0 }
else { Write-Host "$failed type(s) failed"; exit 1 }
