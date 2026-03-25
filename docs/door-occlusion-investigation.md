# Door/Gate Occlusion Investigation

**Date**: 2026-03-25  
**Status**: Partially fixed, root cause still unknown  

## Problem

Closed doors, gates, and trapdoors produce near-zero occlusion. Sound passes through them as if they aren't there. User reports this is a regression from earlier versions.

## Root Cause Analysis

### Bug 1: Override Pattern Mismatch (FIXED)

**BlockOverrides** in `MaterialSoundConfig.cs` used patterns like:
```
"game:door-*-closed-*" → 0.8
"game:door-*-opened-*" → 0.05
```

But VS door block codes have **no state suffix**: `door-solid-aged`, `metaldoor-sleek-windowed-iron`, `door-2x2gate-larch`. The regex `^game:door-.*-closed-.*$` never matches. Without a match:
- `HasBlockOverride()` returns false
- Falls through to wood material = 0.6
- Volume scaling applied: 0.6 × sqrt(0.19) ≈ **0.21** occlusion

**Fix**: Broad prefix patterns (`game:door-*`, `game:metaldoor-*`, `game:trapdoor-*`, `game:*gate*`).

### Bug 2: Saved Config Override (FIXED)

The saved config file `soundphysicsadapted_materials.json` (Version 4) had the broken patterns baked in. Code changes to defaults had no effect because the saved file always wins.

**Fix**: Added v5 config migration that removes broken patterns and adds correct ones.

### Bug 3: Ray-AABB Missing Thin Door Panels (INVESTIGATED, REVERTED)

Door collision boxes are ~3/16 block thick. The slab intersection test (`RayIntersectsAABB`) missed at oblique angles, producing false "ray misses geometry" even for closed doors. When center ray missed → `centerOcclusion < 0.3` → "clear, skip offset" → offset rays never fire → zero occlusion.

**Attempted fix**: Skip `RayHitsAnyCollisionBox` for `IsWeatherInteractable` blocks and apply override occlusion directly.

**Result**: DDA correctly showed `door-hit: occ=0.80` for every ray. BUT the downstream **path resolution system** was finding 16/16 clear probe paths around/over the door, blending effective occlusion down to 0.57. The `bOcc` in `4B-LPF` was capped by path clarity probes finding alternate routes.

**Reverted because**: Even with correct DDA values (0.80), the path resolution system's 25th-percentile blending (`SoundPathResolver.cs` line ~242) reduced effective occlusion. The fix masked the real problem — the path probes shouldn't find clear routes in a fully enclosed room. Needs investigation into why 16/16 probes find open paths when only a closed door exists.

### Bug 4: Path Probe Clarity — RULED OUT

Initially suspected the path resolution system (`SoundPathResolver`) was blending door occlusion down via 25th-percentile clear-path probes. However:

**Testing with sound repositioning OFF** (disables path resolution entirely) produced **identical occlusion values**. The same low effective occlusion plays with or without the path system active.

This rules out `SoundPathResolver` as the culprit. The issue is upstream — somewhere between the DDA hitting the door and the final LPF value being applied, the occlusion is being reduced or ignored. Potential areas:
- The override pattern match is happening but the occlusion value isn't propagating to the final calculation
- Volume scaling is still being applied despite the override check
- The smoothing/EMA system in `AudioPhysicsSystem` is damping door transitions
- Some other code path is overriding the DDA result before LPF application
- The reverted `IsOpenInteractable` check in OcclusionCalculator is skipping door blocks entirely

## What's Currently Deployed

1. **MaterialSoundConfig.cs**: Broad override patterns (KEEP)
2. **SoundPhysicsAdaptedModSystem.cs**: v5 config migration (KEEP)
3. **OcclusionCalculator.cs**: Reverted to pre-investigation state — IsOpenInteractable checks restored, ray-AABB test restored for all non-solid blocks

## Log Evidence

```
# Before fix (override not matching):
DDA hit: game:door-solid-aged occ=0,21          # volume-scaled wood (0.6 × 0.35)
DDA pass-through: game:door-solid-aged (ray misses geometry)  # thin panel miss

# After override fix (correct value when ray hits):
DDA hit: game:door-solid-aged occ=0,80          # correct override value
DDA hit: game:metaldoor-sleek-windowed-iron occ=0,90

# After ray-AABB bypass (always hits):
DDA door-hit: game:door-solid-aged occ=0,80     # every ray hits
# But path system still finds clear routes:
4B-LPF: dOcc=0,83 bOcc=0,57 smooth=0,57 filt=0,562 clarity=100%
4B-Path: off=0,7m bOcc=0,83 paths=16/16 perm=0  # all paths "clear"
```

## Next Steps

1. ~~Investigate path probe clarity~~ — RULED OUT (same values with repositioning off)
2. Trace the full pipeline from DDA hit → final LPF application with logging to find where door occlusion is lost
3. Check if the reverted `IsOpenInteractable` check is causing DDA to skip door blocks
4. Verify override value actually reaches `OcclusionToFilter` and isn't overwritten
5. Check EMA smoothing behavior — is it keeping stale low values from before door was closed?
