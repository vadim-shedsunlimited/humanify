#!/usr/bin/env pwsh
# Build and test script for Humanify C# version

Write-Host "=== Humanify C# Build & Test ===" -ForegroundColor Cyan

# Navigate to the project directory
Set-Location "C:\Users\permi\source\repos\humanify\csharp\Humanify"

# Clean previous builds
Write-Host "`n1. Cleaning previous builds..." -ForegroundColor Yellow
dotnet clean
if ($LASTEXITCODE -ne 0) {
    Write-Host "Clean failed!" -ForegroundColor Red
    exit 1
}

# Restore packages
Write-Host "`n2. Restoring packages..." -ForegroundColor Yellow
dotnet restore
if ($LASTEXITCODE -ne 0) {
    Write-Host "Restore failed!" -ForegroundColor Red
    exit 1
}

# Build the project
Write-Host "`n3. Building project..." -ForegroundColor Yellow
dotnet build --configuration Release
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}

# Run tests (if any exist)
Write-Host "`n4. Running tests..." -ForegroundColor Yellow
dotnet test --configuration Release --verbosity minimal
if ($LASTEXITCODE -ne 0) {
    Write-Host "Tests failed!" -ForegroundColor Red
    # Don't exit, tests might not exist yet
}

# Create a test file
Write-Host "`n5. Creating test file..." -ForegroundColor Yellow
$testContent = @"
function a(e,t){var n=[];var r=e.length;var i=0;for(;i<r;i+=t){if(i+t<r){n.push(e.substring(i,i+t))}else{n.push(e.substring(i,r))}}return n}
"@
$testContent | Out-File -FilePath "test.min.js" -Encoding UTF8
Write-Host "Created test.min.js"

# Test the CLI with help
Write-Host "`n6. Testing CLI help..." -ForegroundColor Yellow
dotnet run -- --help
if ($LASTEXITCODE -ne 0) {
    Write-Host "CLI help failed!" -ForegroundColor Red
    exit 1
}

# Test OpenAI command help
Write-Host "`n7. Testing OpenAI command help..." -ForegroundColor Yellow
dotnet run -- openai --help
if ($LASTEXITCODE -ne 0) {
    Write-Host "OpenAI help failed!" -ForegroundColor Red
    exit 1
}

# Test with a dry run (will fail due to no API key, but shows it works)
Write-Host "`n8. Testing with dry run (expected to fail gracefully)..." -ForegroundColor Yellow
Write-Host "Note: This will fail due to missing API key, but demonstrates the flow works"
dotnet run -- openai test.min.js --apiKey "fake-key" 2>&1 | Out-Null
if ($LASTEXITCODE -eq 0) {
    Write-Host "Unexpected success - should fail with fake API key" -ForegroundColor Yellow
}

# Check if output was created
if (Test-Path "output\test.min.js") {
    Write-Host "`n✓ Output file created successfully!" -ForegroundColor Green
    Write-Host "Content of output:"
    Get-Content "output\test.min.js" | ForEach-Object { Write-Host "  $_" }
}
else {
    Write-Host "`n✗ Output file not created" -ForegroundColor Red
}

Write-Host "`n=== Build & Test Complete ===" -ForegroundColor Cyan
Write-Host "`nTo use the tool:" -ForegroundColor Green
Write-Host "  dotnet run -- openai input.js --apiKey YOUR_API_KEY" -ForegroundColor White
Write-Host "`nTo install as global tool:" -ForegroundColor Green
Write-Host "  dotnet pack" -ForegroundColor White
Write-Host "  dotnet tool install --global --add-source ./nupkg humanify" -ForegroundColor White
