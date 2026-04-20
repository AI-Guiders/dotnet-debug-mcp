# Publish Release (win-x64, self-contained) and mirror to a fixed path for Cursor MCP.
# Run from repo:  cd ...\dotnet-debug-mcp  ;  .\publish-and-deploy.ps1
# Optional: -Target "D:\dotnet-debug-mcp"
[CmdletBinding()]
param(
    [string] $Target = "D:\dotnet-debug-mcp"
)

$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$csproj = Join-Path $here "DotnetDebugMcp.csproj"
if (-not (Test-Path -LiteralPath $csproj)) {
    Write-Error "DotnetDebugMcp.csproj not found. Run this script from the dotnet-debug-mcp directory (PSScriptRoot=$here)."
    exit 1
}

$outDir = Join-Path $here "publish"

Push-Location $here
try {
    $publishArgs = @(
        "publish", $csproj,
        "-c", "Release",
        "-r", "win-x64",
        "-o", $outDir,
        "-v", "minimal"
    )

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    if (-not (Test-Path -LiteralPath $Target)) {
        New-Item -ItemType Directory -Path $Target -Force | Out-Null
    }

    robocopy $outDir $Target /E /MIR /NFL /NDL /NJH /NJS | Out-Null
    $robocode = $LASTEXITCODE
    if ($robocode -ge 8) {
        Write-Error "robocopy failed with exit code $robocode"
        exit $robocode
    }

    $exe = Join-Path $Target "DotnetDebugMcp.exe"
    if (-not (Test-Path -LiteralPath $exe)) {
        Write-Error "Expected exe not found: $exe"
        exit 1
    }

    $ts = (Get-Item -LiteralPath $exe).LastWriteTimeUtc.ToString("o")
    $exeJson = $exe.Replace('\', '\\')
    Write-Host ""
    Write-Host "OK: $exe  (UTC $ts)"
    Write-Host ""
    Write-Host "Cursor MCP: paste into mcp.json ->"
    Write-Host @"
  "dotnet-debug": {
    "command": "$exeJson",
    "args": []
  }
"@
    Write-Host ""
} finally {
    Pop-Location
}
