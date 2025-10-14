$ErrorActionPreference = "SilentlyContinue"
$ts = Get-Date -Format "yyyyMMdd_HHmmss"
$outFile = Join-Path (Get-Location) ("OrionERP_Snapshot_{0}.md" -f $ts)
Remove-Item $outFile -ErrorAction Ignore
function Append($text){ $text | Out-File -FilePath $outFile -Encoding UTF8 -Append }

Append "# OrionERP — Repository Snapshot"
Append ("> Generated: {0}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz'))
Append ""

# Environment
Append "# Environment"
$dotver = (dotnet --version) 2>$null
$sdks   = (dotnet --list-sdks) 2>$null
Append "```text"
Append "dotnet --version:"; if($dotver){Append $dotver}else{Append "(dotnet not found)"}
Append ""; Append "dotnet --list-sdks:"; if($sdks){Append $sdks}else{Append "(no sdks)"}; Append "```"; Append ""

# Git
Append "# Git"
Append "```text"
if (Get-Command git -ErrorAction SilentlyContinue) {
  $branch = (git rev-parse --abbrev-ref HEAD) 2>$null
  $remote = (git remote -v) 2>$null
  $lastCommit = (git log -1 --pretty=format:"%H %an %ad %s" --date=iso) 2>$null
  $status = (git status --porcelain) 2>$null
  Append "Branch: $branch"; Append ""; Append "Remotes:"; Append $remote
  Append ""; Append "Last commit:"; Append $lastCommit
  Append ""; Append "Working tree (porcelain):"; Append $status
} else { Append "Git not found" }
Append "```"; Append ""

# Solution & Projects
Append "# Solution & Projects"
$slnList = (dotnet sln list) 2>$null
Append "```text"; if($slnList){Append $slnList}else{Append "(no solution or dotnet not found)"}; Append "```"; Append ""

# NuGet Packages
Append "# NuGet Packages (solution)"
$pkg = (dotnet list OrionERP.sln package) 2>$null
Append "```text"; if($pkg){Append $pkg}else{Append "(could not list packages)"}; Append "```"; Append ""

# Folder Tree
Append "# Folder Tree"
$tree = (cmd /c "tree /F") 2>$null
Append "```text"; if($tree){Append $tree}else{Append "(tree command failed)"}; Append "```"; Append ""

# Files to include
$files = @(
  ".editorconfig:ini",
  ".gitattributes:gitattributes",
  ".gitignore:gitignore",
  "global.json:json",
  "README.md:md",
  "LICENSE:md",
  ".github/workflows/ci.yml:yaml",
  ".github/PULL_REQUEST_TEMPLATE.md:md",
  ".github/ISSUE_TEMPLATE/feature.md:md",
  ".github/ISSUE_TEMPLATE/bug.md:md",
  "src/OrionERP.Web/OrionERP.Web.csproj:xml",
  "src/OrionERP.Web/Program.cs:csharp",
  "src/OrionERP.Web/appsettings.json:json",
  "src/OrionERP.Web/Properties/launchSettings.json:json",
  "src/OrionERP.Web/Pages/Index.razor:razor",
  "src/OrionERP.Domain/OrionERP.Domain.csproj:xml",
  "src/OrionERP.Application/OrionERP.Application.csproj:xml",
  "src/OrionERP.Infrastructure/OrionERP.Infrastructure.csproj:xml",
  "tests/OrionERP.UnitTests/OrionERP.UnitTests.csproj:xml",
  "tests/OrionERP.UnitTests/MathSmokeTests.cs:csharp",
  "tests/OrionERP.IntegrationTests/OrionERP.IntegrationTests.csproj:xml",
  "tests/OrionERP.IntegrationTests/HealthzTests.cs:csharp"
)
foreach($entry in $files){
  $parts=$entry.Split(":",2); $path=$parts[0]; $lang=$parts[1]
  Append "# File: $path"
  if(Test-Path $path){ Append "```$lang"; (Get-Content -Raw -Path $path) | Out-File -FilePath $outFile -Encoding UTF8 -Append; Append "```" }
  else { Append "_(missing)_" }
  Append ""
}
Append ""; Append "---"; Append "End of snapshot."
Write-Host "Snapshot written to: $outFile" -ForegroundColor Green
Start-Process notepad.exe $outFile | Out-Null
