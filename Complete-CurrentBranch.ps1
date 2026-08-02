[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
Set-Location -LiteralPath $PSScriptRoot

$remoteName = "origin"
$baseBranch = "main"
$expectedRepository = "Jorge-Contreras/OrionERP"

$sourceBranch = $null
$pullRequestUrl = $null
$mergeConfirmed = $false

function Write-Step {
    param([Parameter(Mandatory)][string]$Message)

    Write-Host ""
    Write-Host "=== $Message ===" -ForegroundColor Cyan
}

function Invoke-NativeCommand {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [switch]$CaptureOutput
    )

    if ($CaptureOutput) {
        $output = @(& $FilePath @ArgumentList)
    }
    else {
        & $FilePath @ArgumentList
        $output = @()
    }

    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        $renderedArguments = $ArgumentList -join " "
        throw "Command failed with exit code ${exitCode}: $FilePath $renderedArguments"
    }

    if ($CaptureOutput) {
        return $output
    }
}

function Get-SingleNativeOutput {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$ArgumentList = @()
    )

    $output = @(Invoke-NativeCommand -FilePath $FilePath -ArgumentList $ArgumentList -CaptureOutput)
    return (($output | ForEach-Object { [string]$_ }) -join "`n").Trim()
}

function Test-GitReference {
    param([Parameter(Mandatory)][string]$Reference)

    & git show-ref --verify --quiet $Reference
    $exitCode = $LASTEXITCODE

    if ($exitCode -eq 0) {
        return $true
    }

    if ($exitCode -eq 1) {
        return $false
    }

    throw "Unable to inspect Git reference '$Reference' (exit code $exitCode)."
}

function Test-GitAncestor {
    param(
        [Parameter(Mandatory)][string]$Ancestor,
        [Parameter(Mandatory)][string]$Descendant
    )

    & git merge-base --is-ancestor $Ancestor $Descendant
    $exitCode = $LASTEXITCODE

    if ($exitCode -eq 0) {
        return $true
    }

    if ($exitCode -eq 1) {
        return $false
    }

    throw "Unable to compare '$Ancestor' with '$Descendant' (exit code $exitCode)."
}

function Test-RemoteBranch {
    param(
        [Parameter(Mandatory)][string]$Remote,
        [Parameter(Mandatory)][string]$Branch
    )

    & git ls-remote --exit-code --heads $Remote "refs/heads/$Branch" *> $null
    $exitCode = $LASTEXITCODE

    if ($exitCode -eq 0) {
        return $true
    }

    if ($exitCode -eq 2) {
        return $false
    }

    throw "Unable to inspect '$Remote/$Branch' (exit code $exitCode)."
}

function Get-GitHubRepositoryFromRemoteUrl {
    param([Parameter(Mandatory)][string]$RemoteUrl)

    $patterns = @(
        '^https?://github\.com/(?<repository>[^/]+/[^/]+?)(?:\.git)?/?$',
        '^git@github\.com:(?<repository>[^/]+/[^/]+?)(?:\.git)?$',
        '^ssh://git@github\.com/(?<repository>[^/]+/[^/]+?)(?:\.git)?/?$'
    )

    foreach ($pattern in $patterns) {
        if ($RemoteUrl -match $pattern) {
            return $Matches.repository
        }
    }

    return $null
}

function Assert-NoGitOperationInProgress {
    $stateNames = @(
        "MERGE_HEAD",
        "CHERRY_PICK_HEAD",
        "REVERT_HEAD",
        "REBASE_HEAD",
        "rebase-apply",
        "rebase-merge"
    )

    foreach ($stateName in $stateNames) {
        $statePath = Get-SingleNativeOutput -FilePath "git" -ArgumentList @("rev-parse", "--git-path", $stateName)
        if (Test-Path -LiteralPath $statePath) {
            throw "A Git operation is still in progress ('$stateName'). Complete or abort it before running this script."
        }
    }
}

try {
    Write-Step "Preflight checks"

    if (-not (Get-Command -Name "git" -ErrorAction SilentlyContinue)) {
        throw "Git is required but was not found on PATH."
    }

    if (-not (Get-Command -Name "gh" -ErrorAction SilentlyContinue)) {
        throw "GitHub CLI is required but was not found on PATH. Install it and run 'gh auth login'."
    }

    $insideWorkTree = Get-SingleNativeOutput -FilePath "git" -ArgumentList @("rev-parse", "--is-inside-work-tree")
    if ($insideWorkTree -ne "true") {
        throw "This script must run from the OrionERP Git working tree."
    }

    $repositoryRoot = Get-SingleNativeOutput -FilePath "git" -ArgumentList @("rev-parse", "--show-toplevel")
    $resolvedRepositoryRoot = (Resolve-Path -LiteralPath $repositoryRoot).Path.TrimEnd([char[]]@('\', '/'))
    $resolvedScriptRoot = (Resolve-Path -LiteralPath $PSScriptRoot).Path.TrimEnd([char[]]@('\', '/'))
    if (-not [string]::Equals($resolvedRepositoryRoot, $resolvedScriptRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Complete-CurrentBranch.ps1 must remain in the OrionERP repository root."
    }

    $remoteUrl = Get-SingleNativeOutput -FilePath "git" -ArgumentList @("remote", "get-url", $remoteName)
    $remoteRepository = Get-GitHubRepositoryFromRemoteUrl -RemoteUrl $remoteUrl
    if ([string]::IsNullOrWhiteSpace($remoteRepository) -or
        -not [string]::Equals($remoteRepository, $expectedRepository, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Remote '$remoteName' must point to '$expectedRepository'. Current URL: '$remoteUrl'."
    }

    Invoke-NativeCommand -FilePath "gh" -ArgumentList @("auth", "status", "--hostname", "github.com")

    $repositoryJson = Get-SingleNativeOutput -FilePath "gh" -ArgumentList @(
        "repo", "view", $expectedRepository,
        "--json", "nameWithOwner,viewerPermission"
    )
    $repositoryDetails = $repositoryJson | ConvertFrom-Json
    if (-not [string]::Equals([string]$repositoryDetails.nameWithOwner, $expectedRepository, [StringComparison]::OrdinalIgnoreCase)) {
        throw "GitHub CLI resolved an unexpected repository: '$($repositoryDetails.nameWithOwner)'."
    }

    if ($repositoryDetails.viewerPermission -ne "ADMIN") {
        throw "Administrator permission on '$expectedRepository' is required for an immediate merge bypass."
    }

    Invoke-NativeCommand -FilePath "git" -ArgumentList @("fetch", $remoteName, $baseBranch, "--prune")

    $sourceBranch = Get-SingleNativeOutput -FilePath "git" -ArgumentList @("branch", "--show-current")
    if ([string]::IsNullOrWhiteSpace($sourceBranch)) {
        throw "Detached HEAD is not supported. Switch to a feature branch before running this script."
    }

    if ($sourceBranch -eq $baseBranch) {
        throw "The current branch is '$baseBranch'. Switch to the feature branch that should be committed and merged."
    }

    if (-not (Test-GitReference -Reference "refs/heads/$baseBranch")) {
        throw "The local '$baseBranch' branch does not exist. Create it from '$remoteName/$baseBranch' before continuing."
    }

    if (-not (Test-GitReference -Reference "refs/remotes/$remoteName/$baseBranch")) {
        throw "The remote-tracking branch '$remoteName/$baseBranch' does not exist."
    }

    if (-not (Test-GitAncestor -Ancestor $baseBranch -Descendant "$remoteName/$baseBranch")) {
        throw "Local '$baseBranch' contains commits that are not in '$remoteName/$baseBranch'. Resolve that divergence before continuing."
    }

    $worktreeDetails = @(Invoke-NativeCommand -FilePath "git" -ArgumentList @("worktree", "list", "--porcelain") -CaptureOutput)
    if ($worktreeDetails -contains "branch refs/heads/$baseBranch") {
        throw "The '$baseBranch' branch is checked out in another worktree and cannot be selected here for cleanup."
    }

    Assert-NoGitOperationInProgress

    $conflictedFiles = @(Invoke-NativeCommand -FilePath "git" -ArgumentList @("diff", "--name-only", "--diff-filter=U") -CaptureOutput)
    if ($conflictedFiles.Count -gt 0) {
        throw "Resolve all conflicted files before running this script: $($conflictedFiles -join ', ')"
    }

    $pendingChanges = @(Invoke-NativeCommand -FilePath "git" -ArgumentList @(
        "status", "--porcelain=v1", "--untracked-files=all"
    ) -CaptureOutput)
    if ($pendingChanges.Count -eq 0) {
        throw "There are no tracked, untracked, or deleted files to commit on '$sourceBranch'."
    }

    $existingPullRequestUrl = Get-SingleNativeOutput -FilePath "gh" -ArgumentList @(
        "pr", "list",
        "--repo", $expectedRepository,
        "--head", $sourceBranch,
        "--base", $baseBranch,
        "--state", "open",
        "--limit", "1",
        "--json", "url",
        "--jq", '.[0].url // ""'
    )
    if (-not [string]::IsNullOrWhiteSpace($existingPullRequestUrl)) {
        throw "An open pull request already exists for '$sourceBranch': $existingPullRequestUrl"
    }

    $gitUserName = Get-SingleNativeOutput -FilePath "git" -ArgumentList @("config", "user.name")
    $gitUserEmail = Get-SingleNativeOutput -FilePath "git" -ArgumentList @("config", "user.email")
    if ([string]::IsNullOrWhiteSpace($gitUserName) -or [string]::IsNullOrWhiteSpace($gitUserEmail)) {
        throw "Configure Git user.name and user.email before committing."
    }

    Write-Step "Pending changes on '$sourceBranch'"
    Invoke-NativeCommand -FilePath "git" -ArgumentList @("status", "--short")

    $commitTitle = ([string](Read-Host -Prompt "Commit title for branch '$sourceBranch'")).Trim()
    if ([string]::IsNullOrWhiteSpace($commitTitle)) {
        throw "The commit title cannot be empty. No changes were staged or committed."
    }

    Write-Step "Commit all changes"
    Invoke-NativeCommand -FilePath "git" -ArgumentList @("add", "--all")

    & git diff --cached --quiet --exit-code
    $stagedDiffExitCode = $LASTEXITCODE
    if ($stagedDiffExitCode -eq 0) {
        throw "Git found no staged changes after 'git add --all'."
    }
    if ($stagedDiffExitCode -ne 1) {
        throw "Unable to inspect the staged changes (exit code $stagedDiffExitCode)."
    }

    Invoke-NativeCommand -FilePath "git" -ArgumentList @("commit", "-m", $commitTitle)
    $commitSha = Get-SingleNativeOutput -FilePath "git" -ArgumentList @("rev-parse", "HEAD")

    Write-Step "Push '$sourceBranch'"
    Invoke-NativeCommand -FilePath "git" -ArgumentList @(
        "push", "--set-upstream", $remoteName, $sourceBranch
    )

    $pullRequestBody = @'
## What
- {0}

## Why
- Publish changes from `{1}` to `main`.

## How to test
- GitHub Actions CI starts automatically. This pull request is intentionally merged without waiting for it to complete.
'@ -f $commitTitle, $sourceBranch

    Write-Step "Create pull request to '$remoteName/$baseBranch'"
    Invoke-NativeCommand -FilePath "gh" -ArgumentList @(
        "pr", "create",
        "--repo", $expectedRepository,
        "--base", $baseBranch,
        "--head", $sourceBranch,
        "--title", $commitTitle,
        "--body", $pullRequestBody
    )

    $pullRequestJson = Get-SingleNativeOutput -FilePath "gh" -ArgumentList @(
        "pr", "view", $sourceBranch,
        "--repo", $expectedRepository,
        "--json", "url,state"
    )
    $pullRequest = $pullRequestJson | ConvertFrom-Json
    $pullRequestUrl = [string]$pullRequest.url
    if ([string]::IsNullOrWhiteSpace($pullRequestUrl) -or $pullRequest.state -ne "OPEN") {
        throw "The new pull request could not be resolved as an open pull request."
    }

    Write-Step "Merge pull request without waiting for CI"
    Invoke-NativeCommand -FilePath "gh" -ArgumentList @(
        "pr", "merge", $pullRequestUrl,
        "--repo", $expectedRepository,
        "--merge",
        "--admin",
        "--match-head-commit", $commitSha
    )

    $mergedPullRequestJson = Get-SingleNativeOutput -FilePath "gh" -ArgumentList @(
        "pr", "view", $pullRequestUrl,
        "--repo", $expectedRepository,
        "--json", "url,state,mergedAt,mergeCommit"
    )
    $mergedPullRequest = $mergedPullRequestJson | ConvertFrom-Json
    if ($mergedPullRequest.state -ne "MERGED" -or [string]::IsNullOrWhiteSpace([string]$mergedPullRequest.mergedAt)) {
        throw "GitHub did not confirm that the pull request was merged. No branch cleanup was attempted."
    }
    $mergeConfirmed = $true

    Write-Step "Delete remote branch '$remoteName/$sourceBranch'"
    if (Test-RemoteBranch -Remote $remoteName -Branch $sourceBranch) {
        Invoke-NativeCommand -FilePath "git" -ArgumentList @("push", $remoteName, "--delete", $sourceBranch)
    }
    else {
        Write-Host "Remote branch was already absent."
    }

    Write-Step "Switch to and synchronize '$baseBranch'"
    Invoke-NativeCommand -FilePath "git" -ArgumentList @("switch", $baseBranch)
    Invoke-NativeCommand -FilePath "git" -ArgumentList @("pull", "--ff-only", $remoteName, $baseBranch)

    if (Test-GitReference -Reference "refs/heads/$sourceBranch") {
        if (-not (Test-GitAncestor -Ancestor $sourceBranch -Descendant $baseBranch)) {
            throw "The merged branch is not an ancestor of local '$baseBranch'; refusing to delete it."
        }

        Invoke-NativeCommand -FilePath "git" -ArgumentList @("branch", "--delete", $sourceBranch)
    }

    Invoke-NativeCommand -FilePath "git" -ArgumentList @("fetch", $remoteName, "--prune")

    Write-Step "Verify final state"
    $finalBranch = Get-SingleNativeOutput -FilePath "git" -ArgumentList @("branch", "--show-current")
    $localMainSha = Get-SingleNativeOutput -FilePath "git" -ArgumentList @("rev-parse", "refs/heads/$baseBranch")
    $remoteMainSha = Get-SingleNativeOutput -FilePath "git" -ArgumentList @("rev-parse", "refs/remotes/$remoteName/$baseBranch")

    if ($finalBranch -ne $baseBranch) {
        throw "Cleanup finished on '$finalBranch' instead of '$baseBranch'."
    }

    if ($localMainSha -ne $remoteMainSha) {
        throw "Local '$baseBranch' is not synchronized with '$remoteName/$baseBranch'."
    }

    if (Test-GitReference -Reference "refs/heads/$sourceBranch") {
        throw "Local branch '$sourceBranch' still exists."
    }

    if (Test-GitReference -Reference "refs/remotes/$remoteName/$sourceBranch") {
        throw "Remote-tracking reference '$remoteName/$sourceBranch' still exists."
    }

    if (Test-RemoteBranch -Remote $remoteName -Branch $sourceBranch) {
        throw "Remote branch '$remoteName/$sourceBranch' still exists."
    }

    Write-Host ""
    Write-Host "Completed successfully." -ForegroundColor Green
    Write-Host "Pull request: $pullRequestUrl"
    Write-Host "Merged commit: $($mergedPullRequest.mergeCommit.oid)"
    Write-Host "Current branch: $finalBranch"
    Write-Host "Local and remote main: $localMainSha"
}
catch {
    Write-Host ""
    if ($mergeConfirmed) {
        Write-Host "The pull request was merged, but cleanup did not complete." -ForegroundColor Yellow
        Write-Host "Review the local branch, remote branch, and '$baseBranch' before retrying cleanup."
    }
    else {
        Write-Host "The workflow stopped before a merge was confirmed. No branch cleanup was attempted." -ForegroundColor Yellow
    }

    if (-not [string]::IsNullOrWhiteSpace([string]$pullRequestUrl)) {
        Write-Host "Pull request: $pullRequestUrl"
    }

    throw
}
