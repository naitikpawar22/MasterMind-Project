param(
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$SolutionPath = Join-Path $RepoRoot "GeminiDesktop.sln"
$AppProjectPath = Join-Path $RepoRoot "GeminiDesktop\GeminiDesktop.csproj"
$TestProjectPath = Join-Path $RepoRoot "tests\GeminiDesktop.Tests\GeminiDesktop.Tests.csproj"
$DistDir = Join-Path $RepoRoot "dist"
$PublishDir = Join-Path $DistDir "publish-win-x64"
$IssPath = Join-Path $PSScriptRoot "GeminiDesktop.iss"

Write-Host "=== MyLaptop Release & Installer Pipeline ===" -ForegroundColor Cyan

if (-not $SkipTests) {
    Write-Host "[1/3] Running automated tests..." -ForegroundColor Yellow
    dotnet test $TestProjectPath -c Release --verbosity normal
    if ($LASTEXITCODE -ne 0) {
        throw "Unit tests failed with exit code $LASTEXITCODE."
    }
}

Write-Host "[2/3] Publishing Release win-x64 build..." -ForegroundColor Yellow
if (Test-Path $PublishDir) {
    Remove-Item -Path $PublishDir -Recurse -Force
}

dotnet publish $AppProjectPath `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -o $PublishDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$ExePath = Join-Path $PublishDir "MyLaptop.exe"
if (-not (Test-Path $ExePath)) {
    throw "Expected executable not found at $ExePath"
}

Write-Host "[3/3] Compiling Inno Setup installer..." -ForegroundColor Yellow
$IsccCandidates = @(
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
)

$Iscc = $IsccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $Iscc) {
    $cmd = Get-Command iscc -ErrorAction SilentlyContinue
    if ($cmd) { $Iscc = $cmd.Source }
}

if (-not $Iscc) {
    throw "Inno Setup compiler (ISCC.exe) not found. Install JRSoftware.InnoSetup via winget or https://jrsoftware.org/isinfo.php."
}

& $Iscc $IssPath
if ($LASTEXITCODE -ne 0) {
    throw "ISCC.exe failed with exit code $LASTEXITCODE."
}

[xml]$CsprojXml = Get-Content $AppProjectPath
$AppVersion = $CsprojXml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $AppVersion) { $AppVersion = "3.0.0" }
$SetupExe = Join-Path $DistDir "MyLaptop-Setup-$AppVersion.exe"
Write-Host "=== Build Complete ===" -ForegroundColor Green
Write-Host "Executable: $ExePath"
Write-Host "Installer : $SetupExe"
