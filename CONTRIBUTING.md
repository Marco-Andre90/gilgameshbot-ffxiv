# Contributing to GilgameshBot

## Branching model

`release` is the single long-lived branch and the source of every published
version. It is **protected**: nothing is pushed to it directly — every change
lands through a pull request from a short-lived branch.

```
feature/*  fix/*  chore/*  docs/*  refactor/*      →  PR  →  release  →  tag + GitHub Release
```

Use a descriptive branch name with one of these prefixes:

| Prefix       | For                                            |
|--------------|------------------------------------------------|
| `feature/`   | new functionality                              |
| `fix/`       | bug fixes                                       |
| `chore/`     | build, CI, tooling, dependencies               |
| `docs/`      | documentation only                             |
| `refactor/`  | internal changes with no behaviour change      |

Example: `git switch -c feature/discord-to-game-relay`

## Commit / PR messages — Conventional Commits

Versioning is automatic and driven by [Conventional Commits](https://www.conventionalcommits.org/).
The **PR title** (and ideally your commits) must start with a type so the
release workflow can pick the right SemVer bump:

| Message                         | Release bump | Example new version |
|---------------------------------|--------------|---------------------|
| `fix: …`                        | patch        | 0.1.0 → 0.1.1       |
| `feat: …`                       | minor        | 0.1.0 → 0.2.0       |
| `feat!: …` or `BREAKING CHANGE:`| major        | 0.1.0 → 1.0.0       |
| anything else                   | patch        | 0.1.0 → 0.1.1       |

Scopes are allowed: `feat(mentions): …`, `fix(bridge): …`.

## The workflow

1. Branch off `release`: `git switch release && git pull && git switch -c fix/…`
2. Make your change. Keep the security rules in [`CLAUDE.md`](CLAUDE.md) intact.
3. Build locally:
   ```bash
   dotnet build GilgameshBot/GilgameshBot.csproj -c Release
   ```
   (On non-Windows / CI, add `-p:EnableWindowsTargeting=true` and point
   `DALAMUD_HOME` at an extracted copy of
   `https://goatcorp.github.io/dalamud-distrib/latest.zip`.)
4. Open a PR into `release`. CI (`.github/workflows/ci.yml`) builds it; the
   PR cannot merge until the **build** check is green.
5. On merge, `.github/workflows/release.yml`:
   - computes the next SemVer version from the commits since the last tag,
   - builds the plugin stamped with that version,
   - creates the `vX.Y.Z` tag and a GitHub Release with `latest.zip` attached.

Nothing is committed back to `release` by the release process — the version
lives in the tag and the released artifact, which is why it coexists with
branch protection.

## Forcing a version

To cut a release manually or force a specific bump, run the **Release**
workflow via *Actions → Release → Run workflow* and set the `bump` input
(`major` / `minor` / `patch`).

## Repository setup (one-time, admin)

Branch protection, the default branch, automatic branch deletion after merge,
and removing `main` are applied by a helper script. They require repo-admin
rights and the GitHub CLI (`gh`, which runs on Windows, macOS and Linux):

- Windows (PowerShell): [`scripts/setup-repo.ps1`](scripts/setup-repo.ps1)
- macOS / Linux (bash): [`scripts/setup-repo.sh`](scripts/setup-repo.sh)

See each file's header for usage. Everything the scripts do can also be done
by hand in the GitHub web UI (**Settings → Branches / Rules** and
**Settings → General → Pull Requests**) if you prefer not to use the CLI.
