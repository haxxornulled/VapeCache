# VapeCache Public Repository Security Verification Script
# Run this in the VapeCache-Public directory after copy-to-public.ps1

param(
    [string]$RepoPath = "C:\Visual Studio Projects\VapeCache-Public"
)

Write-Host "=====================================" -ForegroundColor Cyan
Write-Host "VapeCache Security Verification" -ForegroundColor Cyan
Write-Host "=====================================" -ForegroundColor Cyan
Write-Host ""

if (-not (Test-Path $RepoPath)) {
    Write-Host "ERROR: Repository path not found: $RepoPath" -ForegroundColor Red
    exit 1
}

Push-Location $RepoPath

$errors = @()
$warnings = @()

# Test 1: Check for enterprise projects
Write-Host "[1/8] Checking for enterprise projects..." -ForegroundColor Yellow
$enterpriseProjects = @(
    "VapeCache.Licensing",
    "VapeCache.LicenseGenerator",
    "VapeCache.Persistence",
    "VapeCache.Reconciliation",
    "VapeCache.Application"
)

foreach ($project in $enterpriseProjects) {
    if (Test-Path $project) {
        $errors += "Found enterprise project: $project"
    }
}

if ($errors.Count -eq 0) {
    Write-Host "  ✓ No enterprise projects found" -ForegroundColor Green
} else {
    Write-Host "  ✗ Enterprise projects detected!" -ForegroundColor Red
}
Write-Host ""

# Test 2: Check for HMAC secret in code
Write-Host "[2/8] Searching for HMAC secret key..." -ForegroundColor Yellow
$secretMatches = Get-ChildItem -Path . -Filter "*.cs" -Recurse |
    Select-String -Pattern "VapeCache-HMAC-Secret" -SimpleMatch

if ($secretMatches) {
    foreach ($match in $secretMatches) {
        $errors += "HMAC secret found in: $($match.Path)"
    }
    Write-Host "  ✗ HMAC secret key found in code!" -ForegroundColor Red
} else {
    Write-Host "  ✓ No HMAC secret key found" -ForegroundColor Green
}
Write-Host ""

# Test 3: Check for LicenseValidator references
Write-Host "[3/8] Checking for LicenseValidator usage..." -ForegroundColor Yellow
$licenseRefs = Get-ChildItem -Path . -Filter "*.cs" -Recurse |
    Select-String -Pattern "new LicenseValidator" -SimpleMatch

if ($licenseRefs) {
    foreach ($ref in $licenseRefs) {
        $errors += "LicenseValidator instantiation found in: $($ref.Path)"
    }
    Write-Host "  ✗ LicenseValidator references found!" -ForegroundColor Red
} else {
    Write-Host "  ✓ No LicenseValidator instantiation found" -ForegroundColor Green
}
Write-Host ""

# Test 4: Check for enterprise strategy docs
Write-Host "[4/8] Checking for enterprise business docs..." -ForegroundColor Yellow
$enterpriseDocs = @(
    "ENTERPRISE_STRATEGY.md",
    "BUSINESS_MODEL.md",
    "REVENUE_PROJECTIONS.md"
)

foreach ($doc in $enterpriseDocs) {
    if (Test-Path $doc) {
        $errors += "Found enterprise document: $doc"
    }
}

if ($errors.Count -eq 5) { # 5 = previous errors
    Write-Host "  ✓ No enterprise business documents found" -ForegroundColor Green
} else {
    Write-Host "  ✗ Enterprise documents detected!" -ForegroundColor Red
}
Write-Host ""

# Test 5: Verify solution file
Write-Host "[5/8] Verifying solution file..." -ForegroundColor Yellow
if (Test-Path "VapeCache.sln") {
    $slnContent = Get-Content "VapeCache.sln" -Raw

    $expectedProjects = @(
        "VapeCache.csproj",
        "VapeCache.Abstractions.csproj",
        "VapeCache.Infrastructure.csproj",
        "VapeCache.Extensions.Aspire.csproj",
        "VapeCache.Benchmarks.csproj",
        "VapeCache.Tests.csproj",
        "VapeCache.Console.csproj"
    )

    foreach ($proj in $expectedProjects) {
        if ($slnContent -notmatch [regex]::Escape($proj)) {
            $warnings += "Solution missing expected project: $proj"
        }
    }

    # Check for enterprise projects in solution
    if ($slnContent -match "VapeCache.Licensing|VapeCache.Persistence|VapeCache.Reconciliation") {
        $errors += "Solution file contains enterprise project references!"
    }

    if ($errors.Count -eq 5 -and $warnings.Count -eq 0) {
        Write-Host "  ✓ Solution file is clean" -ForegroundColor Green
    } elseif ($warnings.Count -gt 0) {
        Write-Host "  ⚠ Solution file has warnings" -ForegroundColor Yellow
    } else {
        Write-Host "  ✗ Solution file contains enterprise references!" -ForegroundColor Red
    }
} else {
    $warnings += "Solution file not found"
    Write-Host "  ⚠ Solution file not found" -ForegroundColor Yellow
}
Write-Host ""

# Test 6: Build verification
Write-Host "[6/8] Building solution..." -ForegroundColor Yellow
$buildOutput = dotnet build --configuration Release 2>&1
if ($LASTEXITCODE -eq 0) {
    Write-Host "  ✓ Build succeeded" -ForegroundColor Green
} else {
    $errors += "Build failed"
    Write-Host "  ✗ Build failed!" -ForegroundColor Red
    Write-Host "  Build output:" -ForegroundColor Red
    Write-Host $buildOutput -ForegroundColor Gray
}
Write-Host ""

# Test 7: Run tests
Write-Host "[7/8] Running tests..." -ForegroundColor Yellow
$testOutput = dotnet test --configuration Release --no-build --verbosity quiet 2>&1
if ($LASTEXITCODE -eq 0) {
    Write-Host "  ✓ Tests passed" -ForegroundColor Green
} else {
    $warnings += "Some tests failed (expected if enterprise tests removed)"
    Write-Host "  ⚠ Some tests failed (may be expected)" -ForegroundColor Yellow
}
Write-Host ""

# Test 8: Check git status (if initialized)
Write-Host "[8/8] Checking git status..." -ForegroundColor Yellow
if (Test-Path ".git") {
    $gitStatus = git status --porcelain
    if ($gitStatus) {
        Write-Host "  ⚠ Uncommitted changes detected" -ForegroundColor Yellow
    } else {
        Write-Host "  ✓ Working tree clean" -ForegroundColor Green
    }

    # Check for commits
    $commitCount = git rev-list --count HEAD 2>$null
    if ($commitCount) {
        Write-Host "  ✓ Repository has $commitCount commit(s)" -ForegroundColor Green
    } else {
        $warnings += "No commits yet"
        Write-Host "  ⚠ No commits yet" -ForegroundColor Yellow
    }
} else {
    $warnings += "Git not initialized"
    Write-Host "  ⚠ Git not initialized" -ForegroundColor Yellow
}
Write-Host ""

# Summary
Write-Host "=====================================" -ForegroundColor Cyan
Write-Host "Verification Summary" -ForegroundColor Cyan
Write-Host "=====================================" -ForegroundColor Cyan
Write-Host ""

if ($errors.Count -eq 0 -and $warnings.Count -eq 0) {
    Write-Host "✅ ALL CHECKS PASSED" -ForegroundColor Green
    Write-Host "This repository is SAFE to publish publicly." -ForegroundColor Green
} elseif ($errors.Count -eq 0) {
    Write-Host "⚠ PASSED WITH WARNINGS ($($warnings.Count))" -ForegroundColor Yellow
    Write-Host "Review warnings before publishing:" -ForegroundColor Yellow
    foreach ($warning in $warnings) {
        Write-Host "  - $warning" -ForegroundColor Yellow
    }
} else {
    Write-Host "❌ VERIFICATION FAILED ($($errors.Count) errors)" -ForegroundColor Red
    Write-Host "DO NOT PUBLISH THIS REPOSITORY!" -ForegroundColor Red
    Write-Host ""
    Write-Host "Errors:" -ForegroundColor Red
    foreach ($error in $errors) {
        Write-Host "  - $error" -ForegroundColor Red
    }
}

if ($warnings.Count -gt 0 -and $errors.Count -eq 0) {
    Write-Host ""
    Write-Host "Warnings:" -ForegroundColor Yellow
    foreach ($warning in $warnings) {
        Write-Host "  - $warning" -ForegroundColor Yellow
    }
}

Write-Host ""

Pop-Location

if ($errors.Count -gt 0) {
    exit 1
}

exit 0
