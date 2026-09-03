#!/usr/bin/env bash
#
# One-time repository setup for GilgameshBot's release model.
#
# It:
#   1. makes `release` the default branch,
#   2. applies a branch-protection ruleset to `release`
#      (PRs required, direct pushes/force-pushes/deletion blocked,
#       the CI `build` check must pass),
#   3. optionally deletes the old `main` branch.
#
# These use repo-admin GitHub APIs that are not available to the automated
# tooling, so run this yourself once.
#
# Requirements: GitHub CLI (`gh`) authenticated as a repo admin
#   gh auth login          # or: export GH_TOKEN=<admin PAT with 'repo' + 'admin' scope>
#
# Usage:
#   scripts/setup-repo.sh [OWNER/REPO] [--delete-main] [--strict-admin]
#
#   OWNER/REPO       defaults to the repo of the current directory (gh detects it)
#   --delete-main    also delete the `main` branch after switching the default
#   --strict-admin   do NOT let repo admins bypass the PR requirement
#                    (default: admins may bypass, so a solo maintainer isn't locked out)
#
set -euo pipefail

REPO=""
DELETE_MAIN=0
STRICT_ADMIN=0

for arg in "$@"; do
  case "$arg" in
    --delete-main)  DELETE_MAIN=1 ;;
    --strict-admin) STRICT_ADMIN=1 ;;
    -h|--help)      grep '^#' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    */*)            REPO="$arg" ;;
    *)              echo "Unknown argument: $arg" >&2; exit 1 ;;
  esac
done

if ! command -v gh >/dev/null 2>&1; then
  echo "error: GitHub CLI (gh) is required. https://cli.github.com/" >&2
  exit 1
fi

if [ -z "$REPO" ]; then
  REPO="$(gh repo view --json nameWithOwner -q .nameWithOwner)"
fi
echo "Target repository: $REPO"

# --- 1. default branch -------------------------------------------------------
echo "==> Ensuring 'release' branch exists and is the default branch"
if ! gh api "repos/$REPO/branches/release" >/dev/null 2>&1; then
  echo "error: branch 'release' does not exist yet. Create it first, then re-run." >&2
  exit 1
fi
gh api -X PATCH "repos/$REPO" -f default_branch=release >/dev/null
echo "    default branch = release"

# --- 2. branch-protection ruleset -------------------------------------------
echo "==> Applying branch-protection ruleset to 'release'"

if [ "$STRICT_ADMIN" -eq 1 ]; then
  BYPASS='[]'
else
  # actor_id 5 = the built-in "Repository admin" role.
  BYPASS='[{"actor_id":5,"actor_type":"RepositoryRole","bypass_mode":"always"}]'
fi

RULESET_NAME="Protect release"
RULESET_BODY="$(cat <<JSON
{
  "name": "$RULESET_NAME",
  "target": "branch",
  "enforcement": "active",
  "conditions": { "ref_name": { "include": ["refs/heads/release"], "exclude": [] } },
  "bypass_actors": $BYPASS,
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
JSON
)"

# Update the ruleset if one with this name already exists, otherwise create it.
EXISTING_ID="$(gh api "repos/$REPO/rulesets" -q ".[] | select(.name==\"$RULESET_NAME\") | .id" 2>/dev/null | head -n1 || true)"
if [ -n "$EXISTING_ID" ]; then
  echo "    updating existing ruleset #$EXISTING_ID"
  echo "$RULESET_BODY" | gh api -X PUT "repos/$REPO/rulesets/$EXISTING_ID" --input - >/dev/null
else
  echo "    creating ruleset"
  echo "$RULESET_BODY" | gh api -X POST "repos/$REPO/rulesets" --input - >/dev/null
fi
echo "    ruleset '$RULESET_NAME' active"

# --- 3. delete main (optional) ----------------------------------------------
if [ "$DELETE_MAIN" -eq 1 ]; then
  echo "==> Deleting 'main' branch"
  if gh api "repos/$REPO/branches/main" >/dev/null 2>&1; then
    gh api -X DELETE "repos/$REPO/git/refs/heads/main" >/dev/null
    echo "    main deleted"
  else
    echo "    main does not exist; nothing to delete"
  fi
fi

echo "Done."
