# Publish multi-platform builds to GitLab: Generic Package + release asset links.
# Run from repo root on Windows (no Runner). Cross-compiles linux-x64, osx-x64 from Windows.
# Required: GITLAB_URL, GITLAB_TOKEN (or -GitLabUrl, -Token).
# Usage: .\scripts\publish-release-win.ps1 -Version 2026.03.08 -CreateRelease

param(
    [Parameter(Mandatory = $true)]
    [string] $Version,
    [string] $Tag = "v$Version",
    [string] $GitLabUrl,
    [string] $Token,
    [string] $ProjectPath = "Krawler/dotnet-debug-mcp",
    [string[]] $Rids = @("win-x64", "linux-x64", "osx-x64"),
    [switch] $CreateRelease
)

$ErrorActionPreference = "Stop"
$baseUrl = if ($GitLabUrl) { $GitLabUrl.TrimEnd('/') } else { $env:GITLAB_URL?.TrimEnd('/') }
$token  = if ($Token) { $Token } else { $env:GITLAB_TOKEN }
if (-not $baseUrl -or -not $token) { Write-Error "Set GITLAB_URL and GITLAB_TOKEN (or pass -GitLabUrl and -Token)." }
$projectId = $ProjectPath -replace '/', '%2F'
$api = "$baseUrl/api/v4"
$pkgName = "dotnet-debug-mcp"
$zipPaths = @()

foreach ($rid in $Rids) {
    $zipName = "dotnet-debug-mcp-$rid.zip"
    $outDir = "publish-release-temp-$rid"
    if (Test-Path $outDir) { Remove-Item -Recurse -Force $outDir }
    Write-Host "Building $rid ..."
    dotnet publish -c Release -r $rid -o $outDir
    if ($LASTEXITCODE -ne 0) { Write-Warning "dotnet publish -r $rid failed; skipping."; continue }
    $zipPath = Join-Path $PWD $zipName
    if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
    Compress-Archive -Path "$outDir\*" -DestinationPath $zipPath
    Remove-Item -Recurse -Force $outDir
    $zipPaths += @{ Name = $zipName; Path = $zipPath }; Write-Host "  -> $zipName"
}
if ($zipPaths.Count -eq 0) { Write-Error "No builds succeeded." }

foreach ($z in $zipPaths) {
    $uploadUrl = "$api/projects/$projectId/packages/generic/$pkgName/$Version/$($z.Name)"
    Write-Host "Uploading $($z.Name) ..."
    Invoke-RestMethod -Uri $uploadUrl -Method Put -InFile $z.Path -Headers @{ "PRIVATE-TOKEN" = $token } -ContentType "application/octet-stream"
}
if ($CreateRelease) {
    $commitSha = (git rev-parse HEAD).Trim()
    $body = @{ tag_name = $Tag; ref = $commitSha; name = "Release $Tag"; description = "Pre-built: $($Rids -join ', ') (no Runner)." } | ConvertTo-Json
    Invoke-RestMethod -Uri "$api/projects/$projectId/releases" -Method Post -Headers @{ "PRIVATE-TOKEN" = $token } -Body $body -ContentType "application/json"
    Write-Host "Release $Tag created."
}
foreach ($z in $zipPaths) {
    $assetUrl = "$api/projects/$projectId/packages/generic/$pkgName/$Version/$($z.Name)"
    $linkBody = @{ name = $z.Name; url = $assetUrl; link_type = "package" } | ConvertTo-Json
    try { Invoke-RestMethod -Uri "$api/projects/$projectId/releases/$Tag/assets/links" -Method Post -Headers @{ "PRIVATE-TOKEN" = $token } -Body $linkBody -ContentType "application/json; charset=utf-8"; Write-Host "Asset link added: $($z.Name)" }
    catch { Write-Warning "Could not add asset link for $($z.Name): $_" }
}
foreach ($z in $zipPaths) { Remove-Item -Force $z.Path -ErrorAction SilentlyContinue }
Write-Host "Done."
