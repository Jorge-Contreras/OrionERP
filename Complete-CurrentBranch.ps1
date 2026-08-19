[CmdletBinding()]
param(
    # This workflow squash-merges with --admin, so GitHub Actions never gates the
    # change: a local Release build and the full test suites are the only
    # verification that runs before the code reaches main. Skip only when that same
    # verification has just been completed by hand.
    [switch]$SkipVerification,

    # Proceed even when the staged changes contain credential-shaped text. The scan
    # reports what it matched so it can be reviewed rather than silently trusted.
    [switch]$AllowSuspiciousContent
)

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

function Get-OptionalGitConfig {
    param([Parameter(Mandatory)][string]$Name)

    # git config exits 1 when a key is simply absent. Routing this through
    # Invoke-NativeCommand would turn that into a generic "command failed" error and
    # hide the specific guidance below.
    $value = & git config --get $Name
    $exitCode = $LASTEXITCODE

    if ($exitCode -notin @(0, 1)) {
        throw "Unable to read Git configuration '$Name' (exit code $exitCode)."
    }

    return ((@($value) | ForEach-Object { [string]$_ }) -join "").Trim()
}

function Assert-NoSuspiciousStagedContent {
    param([switch]$Allow)

    # Only added lines are inspected: existing history is not this script's problem,
    # but a new secret entering main without review is.
    $addedLines = @(Invoke-NativeCommand -FilePath "git" -ArgumentList @(
        "diff", "--cached", "--unified=0", "--no-color"
    ) -CaptureOutput) | Where-Object { $_ -match '^\+' -and $_ -notmatch '^\+\+\+' }

    # Aimed at credential *literals*. Variable assignments such as
    # "var password = configuredPassword" are code, not secrets, and must not
    # trigger: a scanner that cries wolf gets bypassed reflexively.
    $rules = @(
        # A password inside a connection-string-shaped line. This is exactly the
        # shape that put a live production credential into this repository.
        @{ Name = "connection string password"
           Pattern = '(?i)(?:server|data source|initial catalog|user id)\s*=[^\r\n]*?(?:password|pwd)\s*=\s*(?![\s;"'']|\$|<|\{|%)[^\s;"'']{3,}' },
        @{ Name = "quoted password literal"
           Pattern = '(?i)(?:password|pwd)\s*[:=]\s*["''][^"''\s$<{%][^"'']{2,}["'']' },
        @{ Name = "private key block"; Pattern = '-----BEGIN (?:[A-Z]+ )?PRIVATE KEY-----' },
        @{ Name = "tunnel secret"; Pattern = '(?i)"TunnelSecret"\s*:\s*"[^"]+"' },
        @{ Name = "api token literal"
           Pattern = '(?i)(?:api[_-]?key|access[_-]?token|client[_-]?secret)\s*[:=]\s*["''][^"''\s$<{%][^"'']{7,}["'']' }
    )

    $findings = [System.Collections.Generic.List[string]]::new()
    foreach ($line in $addedLines) {
        foreach ($rule in $rules) {
            if ($line -match $rule.Pattern) {
                $trimmed = $line.Trim()
                if ($trimmed.Length -gt 120) { $trimmed = $trimmed.Substring(0, 120) + "..." }
                $findings.Add("[$($rule.Name)] $trimmed")
                break
            }
        }
    }

    if ($findings.Count -eq 0) {
        Write-Host "No credential-shaped text found in the staged changes."
        return
    }

    Write-Host ""
    Write-Host "Credential-shaped text found in the staged changes:" -ForegroundColor Yellow
    $findings | Select-Object -First 20 | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
    if ($findings.Count -gt 20) {
        Write-Host "  ... and $($findings.Count - 20) more." -ForegroundColor Yellow
    }
    Write-Host ""

    if ($Allow) {
        Write-Host "Continuing because -AllowSuspiciousContent was supplied." -ForegroundColor Yellow
        return
    }

    throw ("Refusing to commit credential-shaped content. Review the lines above. " +
        "Test fixtures and placeholders are fine: re-run with -AllowSuspiciousContent to proceed.")
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

    $gitUserName = Get-OptionalGitConfig -Name "user.name"
    $gitUserEmail = Get-OptionalGitConfig -Name "user.email"
    if ([string]::IsNullOrWhiteSpace($gitUserName) -or [string]::IsNullOrWhiteSpace($gitUserEmail)) {
        throw "Configure Git user.name and user.email before committing."
    }

    if ($SkipVerification) {
        Write-Step "Local verification SKIPPED by request"
        Write-Host "Nothing will check this change before it reaches '$baseBranch'." -ForegroundColor Yellow
    }
    else {
        Write-Step "Local verification (this workflow bypasses CI)"
        Invoke-NativeCommand -FilePath "dotnet" -ArgumentList @("build", "-c", "Release", "--nologo")
        Invoke-NativeCommand -FilePath "dotnet" -ArgumentList @("test", "-c", "Release", "--nologo")
    }

    Write-Step "Pending changes on '$sourceBranch'"
    Invoke-NativeCommand -FilePath "git" -ArgumentList @("status", "--short")

    $changeStat = Get-SingleNativeOutput -FilePath "git" -ArgumentList @("diff", "HEAD", "--shortstat")
    $untrackedCount = @(Invoke-NativeCommand -FilePath "git" -ArgumentList @(
        "ls-files", "--others", "--exclude-standard"
    ) -CaptureOutput).Count
    Write-Host ""
    Write-Host "Tracked changes : $(if ($changeStat) { $changeStat } else { 'none' })"
    Write-Host "New files       : $untrackedCount"

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

    Write-Step "Scan staged changes for credential-shaped text"
    Assert-NoSuspiciousStagedContent -Allow:$AllowSuspiciousContent

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

    Write-Step "Confirm the immediate merge"
    Write-Host "This squash-merges $pullRequestUrl into '$baseBranch' using --admin," -ForegroundColor Yellow
    Write-Host "bypassing branch protection and without waiting for CI, then deletes" -ForegroundColor Yellow
    Write-Host "both the local and remote '$sourceBranch'." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Answering anything else leaves the pull request open for review," -ForegroundColor DarkGray
    Write-Host "with the branch already pushed. Nothing is lost." -ForegroundColor DarkGray
    Write-Host ""
    $mergeAnswer = ([string](Read-Host -Prompt "Type MERGE to merge now")).Trim()
    if (-not [string]::Equals($mergeAnswer, "MERGE", [StringComparison]::Ordinal)) {
        throw "Merge not confirmed. The pull request remains open at $pullRequestUrl."
    }

    Write-Step "Squash merge pull request without waiting for CI"
    Invoke-NativeCommand -FilePath "gh" -ArgumentList @(
        "pr", "merge", $pullRequestUrl,
        "--repo", $expectedRepository,
        "--squash",
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
        $localSourceSha = Get-SingleNativeOutput -FilePath "git" -ArgumentList @("rev-parse", "refs/heads/$sourceBranch")
        if ($localSourceSha -ne $commitSha) {
            throw "Local branch '$sourceBranch' moved after the pull request was created; refusing to force-delete it."
        }

        Invoke-NativeCommand -FilePath "git" -ArgumentList @("branch", "--delete", "--force", $sourceBranch)
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
