Set-StrictMode -Version Latest

function Assert-OrionProductionGitState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,
        [switch]$AllowNonMain,
        [switch]$AllowDirty
    )

    $root = [System.IO.Path]::GetFullPath($RepositoryRoot)
    $insideWorkTree = (& git -C $root rev-parse --is-inside-work-tree 2>$null)
    if ($LASTEXITCODE -ne 0 -or ([string]$insideWorkTree).Trim() -ne "true") {
        throw "The publish script must run from the OrionERP Git working tree."
    }

    $branchOutput = & git -C $root branch --show-current
    if ($LASTEXITCODE -ne 0) {
        throw "The current Git branch could not be determined."
    }
    $branch = if ($null -eq $branchOutput) { "" } else { ([string]$branchOutput).Trim() }
    if (-not $AllowNonMain -and $branch -ne "main") {
        $branchDisplay = if ([string]::IsNullOrWhiteSpace($branch)) { "<detached>" } else { $branch }
        throw "Production publishing requires branch 'main'. Current branch: '$branchDisplay'. Use -AllowNonMain only for an intentional production smoke test."
    }

    $changes = @(& git -C $root status --porcelain)
    if ($LASTEXITCODE -ne 0) {
        throw "The Git working-tree status could not be determined."
    }
    if (-not $AllowDirty -and $changes.Count -gt 0) {
        throw "Production publishing requires a clean working tree. Commit or stash the current changes first."
    }

    if (-not $AllowNonMain) {
        & git -C $root fetch origin main --quiet
        if ($LASTEXITCODE -ne 0) {
            throw "Could not refresh origin/main before production publishing."
        }
        $head = (& git -C $root rev-parse HEAD).Trim()
        $originMain = (& git -C $root rev-parse origin/main).Trim()
        if ($LASTEXITCODE -ne 0 -or $head -ne $originMain) {
            throw "Local main is not identical to origin/main. Pull the latest main before publishing."
        }
    }
}

function Resolve-OrionSafeChildDirectory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$AllowedRoot,
        [Parameter(Mandatory)]
        [string]$Label
    )

    $root = [System.IO.Path]::GetFullPath($AllowedRoot).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $target = [System.IO.Path]::GetFullPath($Path).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $driveRoot = [System.IO.Path]::GetPathRoot($target).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $allowedDriveRoot = [System.IO.Path]::GetPathRoot($root).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)

    if ([string]::IsNullOrWhiteSpace($root) -or
        [string]::IsNullOrWhiteSpace($target) -or
        $root -eq $allowedDriveRoot -or
        $target -eq $driveRoot -or
        $target -eq $root -or
        -not $target.StartsWith(
            $root + [System.IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label must be a non-root child of the approved production directory '$root'."
    }

    # GetFullPath protects the lexical boundary, but an existing junction or
    # symbolic link below that boundary could still redirect robocopy /MIR to a
    # different volume or directory. Dropbox cloud placeholders also use the
    # ReparsePoint attribute, so inspect LinkType/Target instead of rejecting
    # every reparse point.
    $candidate = $target
    while ($candidate.StartsWith(
            $root + [System.IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) {
        if (Test-Path -LiteralPath $candidate) {
            $item = Get-Item -LiteralPath $candidate -Force
            $linkTypeProperty = $item.PSObject.Properties["LinkType"]
            $targetProperty = $item.PSObject.Properties["Target"]
            $hasLinkType = $null -ne $linkTypeProperty -and
                -not [string]::IsNullOrWhiteSpace([string]$linkTypeProperty.Value)
            $hasLinkTarget = $null -ne $targetProperty -and
                $null -ne $targetProperty.Value -and
                @($targetProperty.Value).Count -gt 0 -and
                -not [string]::IsNullOrWhiteSpace([string]$targetProperty.Value)
            if ($hasLinkType -or $hasLinkTarget) {
                throw "$Label may not traverse a symbolic link or junction: '$candidate'."
            }
        }

        $candidate = [System.IO.Path]::GetDirectoryName($candidate)
        if ([string]::IsNullOrWhiteSpace($candidate) -or
            $candidate.Equals($root, [StringComparison]::OrdinalIgnoreCase)) {
            break
        }
    }

    return $target
}
