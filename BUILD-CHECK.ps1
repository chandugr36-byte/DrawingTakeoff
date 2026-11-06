$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "DrawingTakeoff.csproj"
$lumberCatalog = Join-Path $PSScriptRoot "Resources\lumber_catalog.txt"
$hardwareCatalog = Join-Path $PSScriptRoot "Resources\hardware_catalog.txt"

foreach ($catalog in @($lumberCatalog, $hardwareCatalog)) {
    if (-not (Test-Path -LiteralPath $catalog)) {
        throw "Required reference catalog is missing: $catalog"
    }
}
if (-not (Test-Path -LiteralPath $project)) {
    throw "Project file not found: $project"
}

Write-Host "Reference catalogs found." -ForegroundColor Green
Write-Host "Restoring packages..." -ForegroundColor Cyan
dotnet restore $project
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE" }

Write-Host "Building Release..." -ForegroundColor Cyan
dotnet build $project --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }
Write-Host "Build succeeded. Output: bin\Release\net8.0-windows\" -ForegroundColor Green
