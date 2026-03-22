# ProbeForOpenings Performance Analysis

## SpeedScope Data (90s capture, heavily modded server)

| Metric | Value |
|--------|-------|
| **Inclusive time** | 7,679ms (8.53% of main thread) |
| **Call count** | 2,272 |
| **Avg per call** | 3.38ms |
| **Self-time** | ~0ms (all time in child calls) |

## What It Does

`AcousticRaytracer.ProbeForOpenings()` fires 12 probe rays from player toward the sound source direction (with jitter), looking for wall surfaces. For each wall hit, it BFS-searches neighboring blocks for air gaps (openings like windows, doorways). Each found opening then:

1. `CalculatePathOcclusion(opening → player)` — DDA raycast
2. `CalculatePathOcclusion(opening → sound)` — DDA raycast
3. `CountAdjacentAir()` — 6 blockAccessor.GetBlock calls
4. Distance calculations + permeation math

## Why It's Expensive

- **12 probe rays** × `RaycastToSurface` (DDA traversal each)
- Each hit triggers BFS neighbor search (up to ~6+ blocks explored)
- Each air gap found fires **2 full DDA raycasts** (`CalculatePathOcclusion`)
- In a complex modded environment with many surfaces, this cascades into dozens of DDA traversals per call

## When It Runs

```csharp
bool skipProbes = soundDistance > 25f || directOcclusion < 1.0f;
if (!skipProbes)
{
    ProbeForOpenings(soundPos, playerPos, blockAccessor, config);
}
```

Only runs when:
- Sound is within 25m AND
- Direct occlusion ≥ 1.0 (heavily occluded — behind a wall)

## Call Chain

```
ProcessSoundRaycast (753 calls)
  └─ CalculateWithPathsCacheable (1892 calls — includes cell cache misses)
      └─ ProbeForOpenings (2272 calls)
          ├─ RaycastToSurface × 12 (DDA traversals)
          ├─ blockAccessor.GetBlock (BFS neighbor search)
          └─ CalculatePathOcclusion × 2 per opening (DDA raycasts)
```

## Optimization Ideas (Unimplemented — Needs Further Analysis)

### A. Reduce probe count
- Current: 12 probes. Could reduce to 6 for far sounds (15-25m).
- Risk: Miss openings at oblique angles. Need A/B testing for audio quality.

### B. Cell cache should suppress probes
- If `ReverbCellCache` has a valid entry with stored openings, skip probes entirely.
- Currently probes run even on cache misses before the cache stores results.
- Need to verify: does the cell cache path already handle this? (Check `canStore` branch)

### C. Cap BFS expansion depth
- Current BFS has no explicit depth limit beyond the `_openingDedup` set.
- Could limit to depth=2 (only immediate neighbors of wall hit).

### D. Skip second occlusion ray when first is high
- If `occToPlayer > 2.0f`, we already `continue`. But `occToSound` runs unconditionally.
- Could skip `occToSound` when `occToPlayer` is high enough to indicate a dead path.

### E. Amortize across ticks
- Opening positions are spatially stable — they don't change between ticks unless blocks change.
- Could cache discovered openings per (soundCell, playerCell) and only re-probe on block change invalidation.
- This is essentially what the ReverbCellCache does for reverb, but not for opening probes specifically.

### F. Reduce `CalculatePathOcclusion` cost per opening
- Each opening does a full DDA with all the block classification checks.
- Could use a simplified "solid-face-only" DDA for opening validation (skip partial block AABB checks).

## Next Steps

1. Verify which optimization gives best quality/perf tradeoff
2. A/B test probe count reduction (12 → 6) for perceptual impact
3. Check if cell cache already prevents redundant probes (may already partially solve this)
4. Profile again after IsMultiblockDoorSpacer cache fix (may significantly reduce ProbeForOpenings time since DDA raycasts inside it are now cheaper)
