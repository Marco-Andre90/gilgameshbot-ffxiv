<!--
  All changes reach `release` through a pull request from a feature/fix branch.
  Use a Conventional Commit style PR title so the release version bumps correctly:
    feat: ...      -> minor bump
    fix: ...       -> patch bump
    feat!: / BREAKING CHANGE in body -> major bump
-->

## What

<!-- What does this change do? -->

## Why

<!-- Motivation / linked issue. -->

## Phase

<!-- Which roadmap phase does this belong to? (see docs/ROADMAP.md) -->

## Checklist

- [ ] Branch name follows the convention (`feature/…`, `fix/…`, `chore/…`, `docs/…`)
- [ ] `dotnet build GilgameshBot/GilgameshBot.csproj -c Release` passes
- [ ] Security rules in `CLAUDE.md` respected (token never logged, mentions stay an allow-list, game thread never blocks)
- [ ] Docs updated (`README.md` for the shipped phase, `docs/ROADMAP.md` for future work)
