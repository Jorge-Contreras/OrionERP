[CmdletBinding()]
param(
    [string]$Runtime = "win-x64",
    [switch]$SkipServiceControl
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$publishScript = Join-Path -Path $PSScriptRoot -ChildPath "Publish-prod.ps1"

$arguments = @{
    ServiceName = "OrionERP.Bonhomia"
    ProjectPath = "src\OrionERP.Bonhomia.Web\OrionERP.Bonhomia.Web.csproj"
    OutputDirectory = "C:\Users\Orion\Grupo Carpio Dropbox\Grupo Orion\Software\GitHubs\Production\OrionERP.Bonhomia.Web"
    Runtime = $Runtime
}

if ($SkipServiceControl) {
    $arguments.SkipServiceControl = $true
}

& $publishScript @arguments
