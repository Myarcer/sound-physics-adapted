# Sound Physics Adapted — Dev Reference

> **AI agents: Read this file before making any changes to this project.**

## Development Workflow

**Branches:**
- `main` — Release branch. Only receives merges from `dev` via GitHub PR/merge.
- `dev` — Active development. All local work pushes here.
- `v0.1.7`, etc. — Archived snapshots of `dev` at previous release points.

**Day-to-day:** Push to remote `dev` only. Never push directly to `main`.

**Release process:**
1. Work locally on `dev`, push to remote `dev`
2. When ready to release: merge `dev` → `main` on GitHub (PR or manual merge)
3. Create a GitHub release from `main` with the zip from `Releases/`
4. Tag the release (e.g. `v0.1.8`)

## Subtree Commands (from monorepo root)

`git subtree push` is slow (~995 commits to re-split). Use split + push instead:

```
# Push dev (fast method)
git subtree split --prefix projects/sound-physics-adapted -b spa-split 2>$null
git push sound-physics spa-split:dev
git branch -D spa-split

# Merge dev → main: do this on GitHub via PR or:
# gh pr create --repo Myarcer/sound-physics-adapted --base main --head dev --title "v0.1.x"
```

## Remote

```
sound-physics   https://github.com/Myarcer/sound-physics-adapted.git
```

## Build

```
dotnet build soundphysicsadapted.csproj -c Release
```

Output zip lands in `Releases/` and auto-deploys to mods folder.
