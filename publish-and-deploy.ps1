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

Push-Location $here
try {
    # Prefer local tool (repo-pinned), but global install works too.
    if (Test-Path -LiteralPath (Join-Path $here ".config\\dotnet-tools.json")) {
        & dotnet aid-publish -Project $csproj -Target $Target -Runtime "win-x64" -Configuration "Release" -SelfContained -KillRunning
    } else {
        & aid-publish -Project $csproj -Target $Target -Runtime "win-x64" -Configuration "Release" -SelfContained -KillRunning
    }
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $exe = Join-Path $Target "DotnetDebugMcp.exe"
    $exeJson = $exe.Replace('\', '\\')
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
