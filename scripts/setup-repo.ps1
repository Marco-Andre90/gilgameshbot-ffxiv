#!/usr/bin/env pwsh
<#
.SYNOPSIS
  One-time repository setup for GilgameshBot's release model (Windows / PowerShell).

.DESCRIPTION
  Native Windows equivalent of scripts/setup-repo.sh. It:
    1. makes 'release' the default branch,
    2. enables automatic deletion of head branches after merge,
    3. applies a branch-protection ruleset to 'release'
       (PRs required, direct pushes/force-pushes/deletion blocked,
        the CI 'build' check must pass),
    4. optionally deletes the old 'main' branch.

  Requires the GitHub CLI (gh) authenticated as a repo admin:
    winget install --id GitHub.cli      # if gh is not installed
    gh auth login

.PARAMETER Repo
  OWNER/REPO. Defaults to the repository gh detects in the current directory.

.PARAMETER DeleteMain
  Also delete the 'main' branch after switching the default.

.PARAMETER StrictAdmin
  Do NOT let repo admins bypass the PR requirement
  (default: admins may bypass, so a solo maintainer isn't locked out).

.EXAMPLE
  ./scripts/setup-repo.ps1 -Repo Marco-Andre90/gilgameshbot-ffxiv
#>
param(
  [string]$Repo,
  [switch]$DeleteMain,
  [switch]$StrictAdmin
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
  throw "GitHub CLI (gh) is required. https://cli.github.com/"
}

if (-not $Repo) {
  $Repo = gh repo view --json nameWithOwner -q .nameWithOwner
}
Write-Host "Target repository: $Repo"

# --- 1. default branch -------------------------------------------------------
Write-Host "==> Ensuring 'release' exists and is the default branch"
gh api "repos/$Repo/branches/release" | Out-Null
gh api -X PATCH "repos/$Repo" -f default_branch=release | Out-Null
Write-Host "    default branch = release"

# --- 1b. auto-delete merged branches ----------------------------------------
Write-Host "==> Enabling automatic deletion of head branches after merge"
gh api -X PATCH "repos/$Repo" -F delete_branch_on_merge=true | Out-Null
Write-Host "    delete_branch_on_merge = true"

# --- 2. branch-protection ruleset -------------------------------------------
Write-Host "==> Applying branch-protection ruleset to 'release'"

if ($StrictAdmin) {
  $bypass = '[]'
} else {
  # actor_id 5 = the built-in "Repository admin" role.
  $bypass = '[{"actor_id":5,"actor_type":"RepositoryRole","bypass_mode":"always"}]'
}

$rulesetName = 'Protect release'
$body = @"
{
  "name": "$rulesetName",
  "target": "branch",
  "enforcement": "active",
  "conditions": { "ref_name": { "include": ["refs/heads/release"], "exclude": [] } },
  "bypass_actors": $bypass,
  "rules": [
    { "type": "deletion" },
    { "type": "non_fast_forward" },
    { "type": "pull_request", "parameters": {
        "required_approving_review_count": 0,
        "dismiss_stale_reviews_on_push": false,
        "require_code_owner_review": false,
        "require_last_push_approval": false,
        "required_review_thread_resolution": false
    }},
    { "type": "required_status_checks", "parameters": {
        "strict_required_status_checks_policy": true,
        "required_status_checks": [ { "context": "build" } ]
    }}
  ]
}
"@

$existingId = gh api "repos/$Repo/rulesets" -q ".[] | select(.name==`"$rulesetName`") | .id" 2>$null | Select-Object -First 1
if ($existingId) {
  Write-Host "    updating existing ruleset #$existingId"
  $body | gh api -X PUT "repos/$Repo/rulesets/$existingId" --input - | Out-Null
} else {
  Write-Host "    creating ruleset"
  $body | gh api -X POST "repos/$Repo/rulesets" --input - | Out-Null
}
Write-Host "    ruleset '$rulesetName' active"

# --- 3. delete main (optional) ----------------------------------------------
if ($DeleteMain) {
  Write-Host "==> Deleting 'main' branch"
  try {
    gh api "repos/$Repo/branches/main" | Out-Null
    gh api -X DELETE "repos/$Repo/git/refs/heads/main" | Out-Null
    Write-Host "    main deleted"
  } catch {
    Write-Host "    main does not exist; nothing to delete"
  }
}

Write-Host "Done."
