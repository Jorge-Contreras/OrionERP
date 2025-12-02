# publish-prod.ps1
$ErrorActionPreference = 'Stop'

# Go to the folder where this script is located
Set-Location $PSScriptRoot

# Paths
$project = "src\OrionERP.Web\OrionERP.Web.csproj"
$outDir  = "C:\Users\jc_ca\Grupo Carpio Dropbox\Grupo Orion\Software\GitHubs\Production\OrionERP"

Write-Host "=== Cleaning project ==="
dotnet clean $project

Write-Host "=== Publishing project ==="
dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -o $outDir

Write-Host "=== Done ==="
