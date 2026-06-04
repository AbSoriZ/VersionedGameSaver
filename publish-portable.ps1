Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectPath = Join-Path $repoRoot "VersionedGameSaver.csproj"
$publishDir = Join-Path $repoRoot "bin\Release\net8.0-windows\win-x64\publish"
$publishedExe = Join-Path $publishDir "VersionedGameSaver.exe"
$rootExe = Join-Path $repoRoot "VersionedGameSaver.exe"

dotnet publish $projectPath -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

if (-not (Test-Path -LiteralPath $publishedExe)) {
    throw "Publish completed, but the expected executable was not found: $publishedExe"
}

Copy-Item -LiteralPath $publishedExe -Destination $rootExe -Force
Write-Host "Portable executable created: $rootExe"
