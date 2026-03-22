# Sound Physics Adapted — Dev Reference

> **AI agents: Read this file before making any changes to this project.**

## Development Workflow

**Single branch: `main`** — all work happens here. Local is authoritative.

**Day-to-day:**
1. Work locally on `main`
2. Push to `origin main` (force-push if needed — local is truth)
3. For releases: build, create GitHub release with zip from `Releases/`

**No dev branch. No PRs. Simple.**

## Subtree Commands (from monorepo root)

`git subtree push` is slow (~995 commits to re-split). Use split + push instead:

```
# Push to main (fast method)
git subtree split --prefix projects/sound-physics-adapted -b spa-split 2>$null
git push sound-physics spa-split:main
git branch -D spa-split
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
