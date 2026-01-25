# BannerBros Deployment Script
# Run from the project root: .\deploy.ps1

$ErrorActionPreference = "Stop"

$BannerlordPath = "C:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord"
$ModSource = ".\Modules\BannerBros"
$ModDest = "$BannerlordPath\Modules\BannerBros"

Write-Host "=== BannerBros Deploy Script ===" -ForegroundColor Cyan

# Step 1: Git pull
Write-Host "`n[1/5] Pulling latest changes..." -ForegroundColor Yellow
git pull
if ($LASTEXITCODE -ne 0) {
    Write-Host "Git pull failed!" -ForegroundColor Red
    exit 1
}

# Step 2: Build
Write-Host "`n[2/5] Building solution..." -ForegroundColor Yellow
dotnet build BannerBros.sln
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}

# Step 3: Kill Bannerlord processes
Write-Host "`n[3/5] Stopping Bannerlord processes..." -ForegroundColor Yellow
$processes = @("Bannerlord", "Bannerlord.Native", "Watchdog", "CrashUploader.Publish")
foreach ($proc in $processes) {
    Stop-Process -Name $proc -Force -ErrorAction SilentlyContinue
}
# Give processes time to fully exit
Start-Sleep -Seconds 2

# Step 4: Remove old mod
Write-Host "`n[4/5] Removing old mod files..." -ForegroundColor Yellow
if (Test-Path $ModDest) {
    Remove-Item $ModDest -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path $ModDest) {
        Write-Host "Warning: Could not fully remove old mod. Some files may be locked." -ForegroundColor Red
        Write-Host "Please close any programs using the mod files and try again." -ForegroundColor Red
        exit 1
    }
}

# Step 5: Copy new mod
Write-Host "`n[5/5] Copying new mod files..." -ForegroundColor Yellow
Copy-Item -Path $ModSource -Destination $ModDest -Recurse

# Verify
Write-Host "`n=== Deployment Complete ===" -ForegroundColor Green
Write-Host "Mod installed to: $ModDest"
Write-Host "`nInstalled files:"
Get-ChildItem "$ModDest\bin\Win64_Shipping_Client\*.dll" | ForEach-Object { Write-Host "  - $($_.Name)" }

Write-Host "`nYou can now launch Bannerlord!" -ForegroundColor Cyan
