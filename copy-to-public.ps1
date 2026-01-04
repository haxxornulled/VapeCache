# VapeCache Open Source Repository Copy Script
# This script copies only OSS projects to a clean public repository
# SECURITY: Excludes all enterprise projects and licensing code

$sourceRoot = "C:\Visual Studio Projects\VapeCache"
$targetRoot = "C:\Visual Studio Projects\VapeCache-Public"

Write-Host "=====================================" -ForegroundColor Cyan
Write-Host "VapeCache OSS Repository Copy Script" -ForegroundColor Cyan
Write-Host "=====================================" -ForegroundColor Cyan
Write-Host ""

# Projects to copy (OSS only)
$projectsToCopy = @(
    "VapeCache",
    "VapeCache.Abstractions",
    "VapeCache.Infrastructure",
    "VapeCache.Extensions.Aspire",
    "VapeCache.Benchmarks",
    "VapeCache.Tests",
    "VapeCache.Console"
)

# Directories to copy
$dirsToCopy = @(
    "docs",
    "samples"
)

# Files to copy (root level)
$filesToCopy = @(
    "README.md",
    "LICENSE",
    ".gitignore",
    ".editorconfig",
    "global.json",
    "Directory.Build.props"
)

# Create target directory
if (Test-Path $targetRoot) {
    Write-Host "WARNING: Target directory already exists: $targetRoot" -ForegroundColor Yellow
    $continue = Read-Host "Delete and recreate? (yes/no)"
    if ($continue -ne "yes") {
        Write-Host "Aborted." -ForegroundColor Red
        exit 1
    }
    Remove-Item -Path $targetRoot -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $targetRoot | Out-Null
Write-Host "Created target directory: $targetRoot" -ForegroundColor Green
Write-Host ""

# Copy projects
Write-Host "Copying OSS projects..." -ForegroundColor Green
foreach ($project in $projectsToCopy) {
    $source = Join-Path $sourceRoot $project
    $target = Join-Path $targetRoot $project

    if (Test-Path $source) {
        Write-Host "  ✓ $project" -ForegroundColor Cyan
        Copy-Item -Path $source -Destination $target -Recurse -Force

        # Clean bin/obj directories
        $binPath = Join-Path $target "bin"
        $objPath = Join-Path $target "obj"
        if (Test-Path $binPath) { Remove-Item -Path $binPath -Recurse -Force }
        if (Test-Path $objPath) { Remove-Item -Path $objPath -Recurse -Force }
    } else {
        Write-Host "  ✗ $project (not found)" -ForegroundColor Red
    }
}
Write-Host ""

# Copy directories
Write-Host "Copying documentation..." -ForegroundColor Green
foreach ($dir in $dirsToCopy) {
    $source = Join-Path $sourceRoot $dir
    $target = Join-Path $targetRoot $dir

    if (Test-Path $source) {
        Write-Host "  ✓ $dir/" -ForegroundColor Cyan
        Copy-Item -Path $source -Destination $target -Recurse -Force
    } else {
        Write-Host "  ✗ $dir/ (not found)" -ForegroundColor Yellow
    }
}
Write-Host ""

# Copy root files
Write-Host "Copying root files..." -ForegroundColor Green
foreach ($file in $filesToCopy) {
    $source = Join-Path $sourceRoot $file
    $target = Join-Path $targetRoot $file

    if (Test-Path $source) {
        Write-Host "  ✓ $file" -ForegroundColor Cyan
        Copy-Item -Path $source -Destination $target -Force
    } else {
        Write-Host "  ✗ $file (not found)" -ForegroundColor Yellow
    }
}
Write-Host ""

# Create new solution file (OSS projects only)
Write-Host "Creating clean solution file..." -ForegroundColor Green
Push-Location $targetRoot

# Create new solution
dotnet new sln -n VapeCache --force | Out-Null

# Add OSS projects
foreach ($project in $projectsToCopy) {
    $csproj = Join-Path $targetRoot "$project\$project.csproj"
    if (Test-Path $csproj) {
        dotnet sln add $csproj | Out-Null
        Write-Host "  ✓ Added $project to solution" -ForegroundColor Cyan
    }
}

Pop-Location
Write-Host ""

# Security verification
Write-Host "Running security verification..." -ForegroundColor Yellow
Write-Host ""

$securityIssues = @()

# Check for enterprise projects
$enterpriseProjects = @(
    "VapeCache.Licensing",
    "VapeCache.LicenseGenerator",
    "VapeCache.Persistence",
    "VapeCache.Reconciliation",
    "VapeCache.Application"
)

foreach ($project in $enterpriseProjects) {
    $path = Join-Path $targetRoot $project
    if (Test-Path $path) {
        $securityIssues += "FOUND ENTERPRISE PROJECT: $project"
    }
}

# Check for enterprise files
$enterpriseFiles = @(
    "ENTERPRISE_STRATEGY.md",
    "BUSINESS_MODEL.md",
    "REVENUE_PROJECTIONS.md"
)

foreach ($file in $enterpriseFiles) {
    $path = Join-Path $targetRoot $file
    if (Test-Path $path) {
        $securityIssues += "FOUND ENTERPRISE FILE: $file"
    }
}

# Check for HMAC secret in code
$secretFound = Get-ChildItem -Path $targetRoot -Filter "*.cs" -Recurse |
    Select-String -Pattern "VapeCache-HMAC-Secret" -SimpleMatch

if ($secretFound) {
    $securityIssues += "FOUND HMAC SECRET KEY in code!"
}

# Report security check results
if ($securityIssues.Count -gt 0) {
    Write-Host "❌ SECURITY ISSUES DETECTED:" -ForegroundColor Red
    foreach ($issue in $securityIssues) {
        Write-Host "  - $issue" -ForegroundColor Red
    }
    Write-Host ""
    Write-Host "DO NOT publish this repository until issues are resolved!" -ForegroundColor Red
} else {
    Write-Host "✅ Security verification passed - No enterprise code detected" -ForegroundColor Green
}
Write-Host ""

# Summary
Write-Host "=====================================" -ForegroundColor Cyan
Write-Host "Summary" -ForegroundColor Cyan
Write-Host "=====================================" -ForegroundColor Cyan
Write-Host "Source:      $sourceRoot" -ForegroundColor White
Write-Host "Target:      $targetRoot" -ForegroundColor White
Write-Host "Projects:    $($projectsToCopy.Count) OSS projects copied" -ForegroundColor White
Write-Host "Security:    $(if ($securityIssues.Count -eq 0) { 'PASSED ✓' } else { 'FAILED ✗' })" -ForegroundColor $(if ($securityIssues.Count -eq 0) { 'Green' } else { 'Red' })
Write-Host ""

# Next steps
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  1. cd '$targetRoot'" -ForegroundColor White
Write-Host "  2. dotnet restore" -ForegroundColor White
Write-Host "  3. dotnet build --configuration Release" -ForegroundColor White
Write-Host "  4. dotnet test --configuration Release" -ForegroundColor White
Write-Host "  5. git init && git checkout -b main" -ForegroundColor White
Write-Host "  6. git add ." -ForegroundColor White
Write-Host "  7. git commit -m 'Initial commit: VapeCache v1.0.0'" -ForegroundColor White
Write-Host "  8. gh repo create haxxornulled/VapeCache --public --source=. --remote=origin" -ForegroundColor White
Write-Host "  9. git push -u origin main" -ForegroundColor White
Write-Host ""
Write-Host "Done! Clean OSS repository ready for public release." -ForegroundColor Green
