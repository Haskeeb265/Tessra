$ErrorActionPreference = 'Stop'

$dir = Split-Path (Resolve-Path $PSScriptRoot)
Set-Location $dir

$cloudflared = $env:CLOUDFLARED
if (-not $cloudflared) { $cloudflared = Join-Path $HOME 'cloudflared.exe' }

if (-not (Test-Path $cloudflared)) {
    Write-Error "cloudflared not found at $cloudflared (set $env:CLOUDFLARED to override)."
    exit 1
}

Write-Host "Starting cloudflared tunnel to :80 ..." -ForegroundColor Yellow

$tunnelFile = Join-Path $dir ".tunnel_url.txt"
$job = Start-Job -ScriptBlock {
    param($exe, $file)
    & $exe tunnel --url http://localhost:80 > $file 2>&1
} -ArgumentList $cloudflared, $tunnelFile

$url = $null
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep 1
    $text = Get-Content $tunnelFile -Raw -ErrorAction SilentlyContinue
    if ($text -match 'https://[a-z0-9-]+(?:-[a-z0-9-]+)*\.trycloudflare\.com') {
        $url = $Matches[0]
        break
    }
}

Remove-Job $job -Force -ErrorAction SilentlyContinue

if (-not $url) {
    Write-Error "Tunnel did not print a https://....trycloudflare.com URL after 40s."
    exit 1
}

Write-Host "Tunnel: $url" -ForegroundColor Green

"`nORIGIN=$url" | Out-File "$dir\.env" -Encoding utf8 -Force
Write-Host "Wrote ORIGIN=$url into $dir\.env"
Write-Host "Start the Next.js portal on :3000 (if not already) then:"
Write-Host "    cd apps/platform; docker compose up -d --build"
Write-Host "`nAdd the connector in claude.ai -> Settings -> Connectors:"
Write-Host "   URL: $url/t/acme-dental/mcp"
Write-Host "   Auth: Sign in now (Detected)"
Write-Host "   OAuth client: Use Claude's published identity (Recommended)"
